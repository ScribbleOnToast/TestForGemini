namespace DigitalEye;

using DigitalEye.Helpers;
using DigitalEye;
using System.Diagnostics;
using SherpaOnnx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pv;
using System;
using System.Collections.Generic;
using DigitalEye.Services;

public class Worker(ILogger<Worker> logger, 
    IBluetoothAudioGuard audioGuard, 
    IHostApplicationLifetime appLifetime, 
    IConfiguration configuration, 
    Speaker speaker, 
    RouterService routerService)
    : BackgroundService
{

#region Worker Properties and Fields
    //VLM backend process and socket
    private Process? _pythonProcess;
    private Socket? _socket;
    private const string SocketPath = "/tmp/digitaleye_vision.sock";
    private const string PythonScript = "vision_interface.py";    

    //DI stuff
    private readonly ILogger<Worker> _logger = logger;
    private readonly IBluetoothAudioGuard _audioGuard = audioGuard;
    private readonly IHostApplicationLifetime _appLifetime = appLifetime;
    private readonly IConfiguration _configuration = configuration;
    private RouterService _routerService = routerService;
    private readonly Speaker _speaker = speaker;

    // Paths and Config for STT and TTS
    private const string STTModelPath = "model/stt/";
    private OfflineRecognizer? _recognizer;
    #endregion

#region Lifecycle Methods
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Announce("Initializing Digital Eye...", true);
        // 2. Check Audio Input
        string? micDevice = _audioGuard.EnsureInputReady();
        if (string.IsNullOrEmpty(micDevice))
        {
            await Announce("Audio Input not detected. Exiting.");
            _appLifetime.StopApplication();
            return;
        }
        if (!Directory.Exists(STTModelPath))
        {
            await Announce("Speech Models not found. Exiting.");
            _appLifetime.StopApplication();
            return;
        }
        await StartPythonEngine(stoppingToken);
        _ = StartListeningForBrainResponsesAsync(stoppingToken);
        await _routerService.WarmUpAsync();
        await StartListeningLoopAsync(micDevice, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleaning up worker resources.");
        _audioGuard.StopShadowMonitor();
        _socket?.Close();
        try
        {
            // Attempt to kill the entire process tree where supported
            _pythonProcess?.Kill(entireProcessTree: true);
        }
        catch
        {
            try { _pythonProcess?.Kill(); } catch { }
        }
        _recognizer?.Dispose();
        _recognizer = null;
        return base.StopAsync(cancellationToken);
    }
    #endregion

    #region init routines
    private async Task StartPythonEngine(CancellationToken ct)
    {
        await Announce("Starting Camera System. This may take 20 to 30 seconds.");
        File.Delete(SocketPath); // Clean up old socket if exists

        #region Start Python Process
        // Start the venv python directly (avoids an extra shell and makes shutdown predictable)
        //var venvPath = Path.Combine(AppContext.BaseDirectory, "venv");
        var venvPath = Path.Combine("/opt/digitaleye/venv");
        var pythonExe = System.IO.Path.Combine(venvPath, "bin", "python");

        var start = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"-u {PythonScript}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = "./pyscripts"
        };

        // Ensure the venv bin is at the front of PATH so the venv environment is used
        try
        {
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            start.Environment["VIRTUAL_ENV"] = venvPath;
            start.Environment["PATH"] = System.IO.Path.Combine(venvPath, "bin") + ":" + existingPath;
        }
        catch { }

        _pythonProcess = new Process { StartInfo = start };

        // Pipe Python logs to C# Logger
        _pythonProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogInformation($"[BRAIN LOG]: {e.Data}"); };
        _pythonProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Libcamera and HailoRT are noisy on stderr. 
                // If it says "INFO" or "WARN", just log it as info/warning, not an error.
                if (e.Data.Contains("INFO") || e.Data.Contains("WARN"))
                {
                    _logger.LogInformation($"[BRAIN LOG]: {e.Data}");
                }
                else
                {
                    // Real errors (tracebacks, crashes, critical failures)
                    _logger.LogError($"[BRAIN ERR]: {e.Data}");
                }
            }
        };

        _pythonProcess.Start();
        _pythonProcess.BeginOutputReadLine();
        _pythonProcess.BeginErrorReadLine();

        _logger.LogDebug($"DigitalEye Brain started (PID: {_pythonProcess.Id})");
        #endregion

        #region Wait for VLM ready
        while (!File.Exists(SocketPath) && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Brain Socket not found yet, waiting...");
            await Task.Delay(1000, ct);
        }

        _logger.LogDebug("Connecting to unix socket socket...{SocketPath}", SocketPath);
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(SocketPath);

        int attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _socket.ConnectAsync(endpoint, ct);
                _logger.LogDebug("Connected to unix socket!");
                break;
            }
            catch
            {
                attempts++;
                if (attempts % 5 == 0) _logger.LogWarning("Connecting...");
                await Task.Delay(1000, ct);
            }
        }

        var vlmready = false;
        _ = Task.Run(async () =>
        {
            while (!vlmready)
            {
                _ = _speaker.PlayBeep("confirmation");
                await Task.Delay(3000, ct);
            }
        });

        while (!ct.IsCancellationRequested && !vlmready)
        {
            _logger.LogDebug("Waiting for Brain to signal ready...");
            if (_socket != null && _socket.Connected)
            {
                var buffer = new byte[4096];
                var bytesRead = await _socket.ReceiveAsync(buffer, SocketFlags.None);
                var jsonRes = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // C. Parse
                var doc = JsonSerializer.Deserialize<VisionResponse>(jsonRes);
                if (doc!.Type == "ready")
                {
                    _logger.LogDebug("python interface and backend are ready!");
                    vlmready = true;
                }
            }
            await Task.Delay(3000, ct);
        }
        #endregion
    }

    public async Task StopPythonEngineAsync()
    {
        try
        {
            _ = Announce("Stopping Digital Eye Engine.");
            if (_pythonProcess != null && !_pythonProcess.HasExited)
            {
                _logger.LogInformation("Stopping Digital Eye Engine...");
                try
                {
                    // Attempt graceful shutdown first
                    _pythonProcess.Close();
                    if (!_pythonProcess.WaitForExit(5000))
                    {
                        _logger.LogWarning("Python Brain did not exit gracefully. Attempting to kill...");
                        _pythonProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)            {
                    _logger.LogError(ex, "Error while stopping Python Brain: {Message}", ex.Message);
                    try { _pythonProcess.Kill(); } catch { }
                }
            }
            await Announce("Digital Eye Engine stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while stopping Python Brain: {Message}", ex.Message);
        }
        finally
        {
            _pythonProcess = null;
            _socket?.Close();
            _socket = null;
        }
    }

#endregion

#region Core Logic (The Brain)
    private async Task HandleVoiceCommandAsync(RouteCommandResult cmd, CancellationToken cancellationToken)
    {
        switch (cmd.Intent) 
        {
            case CommandIntent.System:
                var sysPayload = cmd.Payload as SystemPayload;
                if (sysPayload != null)
                {
                    _ = Announce($"Executing system command: {sysPayload.Action} with value {sysPayload.Value}");
                    // Here you would add logic to handle each system command, e.g.:
                    switch (sysPayload.Action)
                    {
                        case SystemCommand.Volume_Up:
                            // Increase volume logic
                            break;
                        case SystemCommand.Volume_Down:
                            // Decrease volume logic
                            break;
                        case SystemCommand.Volume_Set:
                            // Set volume to sysPayload.Value
                            break;
                        case SystemCommand.Mute:
                            // Mute audio
                            break;
                        case SystemCommand.Unmute:
                            // Unmute audio
                            break;
                        case SystemCommand.Shutdown:
                            await StopPythonEngineAsync();
                            _appLifetime.StopApplication();
                            break;
                    }
                }
                break;
            case CommandIntent.Override:
                var overridePayload = cmd.Payload as OverridePayload;
                if (overridePayload != null)                
                {
                    _ = Announce($"Executing override command: {overridePayload.Action}");
                    // Here you would add logic to handle each override command, e.g.:
                    switch (overridePayload.Action)          
                    {
                        case OverrideCommand.Stop:
                            // Stop audio logic
                            break;
                        case OverrideCommand.Pause:
                            // Pause audio logic
                            break;
                        case OverrideCommand.Skip:  
                            // Skip audio logic
                            break;
                        case OverrideCommand.Play:
                            // Play audio logic
                            break;
                    }
                }
                break;
        }
    }

    public async Task GetVLMInference(string prompt = "Describe the scene")
    {
        try
        {
            _logger.LogDebug("Getting VLM response: {Prompt}", prompt);
            if (_socket == null || !_socket.Connected)
            {
                _logger.LogError("Error: VLM Offline");
                await Announce("Error: VLM Offline");
                return;
            }
            var jsonReq = JsonSerializer.Serialize(prompt) + "\n"; // newline-delimited
            var reqBytes = Encoding.UTF8.GetBytes(jsonReq);
            int bytesSent = await _socket.SendAsync(new ArraySegment<byte>(reqBytes), SocketFlags.None);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Inference failed: {ex.Message}");
            await Announce("Error: Inference failed");
        }
    }
#endregion

#region Audio Loop (The Ear)
    /// This method continuously listens to the microphone.
    private async Task StartListeningLoopAsync(string micDevice, CancellationToken stoppingToken)
    {
        await Announce("Starting Listener", false);        

        bool showVisualizer = false;
        float vadThreshold = 0.2f; // Try lowering this (Default is 0.5)
        var vadConfig = new VadModelConfig();
        vadConfig.SileroVad.Model = Path.Combine(STTModelPath,"SileroVad","silero_vad.onnx");
        // Tuning parameters (optional, defaults are usually good)
        vadConfig.SileroVad.Threshold = vadThreshold; 
        vadConfig.SileroVad.MinSilenceDuration = 1.0f; // Seconds of silence to consider "done"
        vadConfig.SileroVad.MinSpeechDuration = 0.1f;

        var vad = new VoiceActivityDetector(vadConfig,15.0f);

        var config = new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig { SampleRate = 16000, FeatureDim = 80 },
            ModelConfig = new OfflineModelConfig
            {
                Moonshine = new OfflineMoonshineModelConfig
                {
                    Preprocessor = Path.Combine(STTModelPath, "Moonshine", "preprocess.onnx"),
                    Encoder = Path.Combine(STTModelPath, "Moonshine", "encode.int8.onnx"),
                    UncachedDecoder = Path.Combine(STTModelPath, "Moonshine", "uncached_decode.int8.onnx"),
                    CachedDecoder = Path.Combine(STTModelPath, "Moonshine", "cached_decode.int8.onnx"), 
                },
                Tokens = Path.Combine(STTModelPath, "Moonshine","tokens.txt"),
                NumThreads = 2,
                Provider = "cpu",
                ModelType = "moonshine",
                Debug = 0
            },
            DecodingMethod = "greedy_search"
        };

        using (var recognizer = new OfflineRecognizer(config))
        {
            // Create a dummy stream for warmup
            var warmupStream = recognizer.CreateStream();
            // Generate 2 seconds of silence (32000 samples at 16kHz)
            float[] silence = new float[16000 * 2]; 
            // Feed it to the engine
            warmupStream.AcceptWaveform(16000, silence);
            recognizer.Decode(warmupStream);
            // Dispose of the dummy stream
            warmupStream.Dispose();

            var stream = recognizer.CreateStream();

            using (var recorder = PvRecorder.Create(frameLength: 512, deviceIndex: -1))
            {
                recorder.Start();
                _ = Announce("Audio engine started. Digital Eye is listening...");
                // Buffer to accumulate speech segments
                List<float> speechBuffer = new List<float>();
                bool isSpeaking = false;

                while (!stoppingToken.IsCancellationRequested && recorder.IsRecording)
                {
                    // A. Read & Normalize
                    short[] rawFrame = recorder.Read();
                    float[] floatFrame = new float[rawFrame.Length];
                    float sumSquares = 0f;
                    for (int i = 0; i < rawFrame.Length; i++)
                    {
                        float val = rawFrame[i] / 32768.0f;
                        floatFrame[i] =val;
                        sumSquares += val * val;
                    }
                    float rms = (float)Math.Sqrt(sumSquares / rawFrame.Length);
                    // B. Feed VAD
                    vad.AcceptWaveform(floatFrame);
                    if (showVisualizer)
                    {
                        // Convert RMS to a simple bar (scale multiplier 100 for visibility)
                        int barLength = Math.Min(50, (int)(rms * 200)); 
                        string bar = new string('#', barLength).PadRight(50);
                        
                        // Check if VAD has data buffered (it means it thinks you are speaking)
                        string status = vad.IsEmpty() ? ".." : "SPEECH DETECTED";
                        
                        // \r overwrites the current line
                        Console.Write($"\r[{bar}] {status}");
                    }
                    // C. If VAD detects speech, it outputs "Segments"
                    while (!vad.IsEmpty())
                    {
                        var segment = vad.Front();
                        vad.Pop();
                        
                        // Add valid speech to our buffer
                        speechBuffer.AddRange(segment.Samples);
                        
                        if (!isSpeaking) {
                            Console.Write("User Speaking... ");
                            isSpeaking = true;
                        }
                    }

                    // D. Processing Logic: 
                    // If the VAD buffer is empty, it means the current "chunk" of speech is over.
                    // If we have data in 'speechBuffer', it means we just finished a sentence.
                    if (vad.IsEmpty() && speechBuffer.Count > 0)
                    {
                        Console.WriteLine($"Processing {speechBuffer.Count} samples...");
                        
                        // Feed the accumulated speech to Moonshine
                        stream.AcceptWaveform(16000, speechBuffer.ToArray());
                        recognizer.Decode(stream);
                        var result = stream.Result.Text.ToLowerInvariant().RemovePunctuation();
                        
                        if (!string.IsNullOrWhiteSpace(result))
                        {
                            Console.WriteLine($"Recognized: {result}");
                            await ProcessRecognizedText(result, stoppingToken);
                        }
                        else
                        {
                            Console.WriteLine("No recognizable speech detected.");
                        }
                        
                        // Cleanup for next phrase
                        speechBuffer.Clear();
                        // Reset the stream to clear context for the next sentence
                        stream = recognizer.CreateStream(); 
                        isSpeaking = false;
                    }
                }
            }  
        }
    }

    private async Task ProcessRecognizedText(string text, CancellationToken token)
    {
        _ =_speaker.PlayBeep("confirmation");
        var intentData = await _routerService.RouteCommandAsync(text);
        switch (intentData?.Intent)
        {
            case CommandIntent.Identify:
                _ = GetVLMInference(intentData.Payload is IdentifyPayload idp ? idp.RawInput : text);
                break;
            case CommandIntent.Error:
                await Announce(intentData.Payload is ErrorPayload errorPayload ? errorPayload.Message : "An error occurred.", false);
                break;
            case CommandIntent.Override:
            case CommandIntent.System:
                await HandleVoiceCommandAsync(intentData, token );
                break;
            default:
                await Announce("Sorry, I didn't understand that command.", false);
                break;        
        }
    }

    /// This method continuously listens for responses from the Brain.
    private async Task StartListeningForBrainResponsesAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_socket == null) return;
            var messageBuffer = new StringBuilder();
            var buffer = new byte[4096];

            while (!stoppingToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
                }
                catch
                {
                    break;
                }

                if (bytesRead <= 0) break;

                messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                string currentContent = messageBuffer.ToString();
                int newlineIndex;

                // While there is a newline, we have at least one full message
                while ((newlineIndex = currentContent.IndexOf('\n')) != -1)
                {
                    // Extract the line
                    string jsonLine = currentContent.Substring(0, newlineIndex).Trim();

                    // Remove processed line from buffer (including the \n)
                    // We use the StringBuilder Remove for efficiency on the main buffer
                    messageBuffer.Remove(0, newlineIndex + 1);
                    
                    // Update local string for the next loop check
                    currentContent = messageBuffer.ToString();

                    if (string.IsNullOrWhiteSpace(jsonLine)) continue;

                    try 
                    {
                        // 4. Parse the isolated JSON line
                        var doc = JsonSerializer.Deserialize<VisionResponse>(jsonLine);
                        if (doc?.Payload != null)
                        {
                            _logger.LogInformation("Received from Brain: {Line}", doc.Payload.Answer);
                            
                            // Fire and forget (or await if you need sequential ordering)
                            await Announce(doc.Payload.Answer); 
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning($"Malformed JSON received: {ex.Message}");
                    }
                }                
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Brain response listener: {Message}", ex.Message);
        }
    }

    private async Task Announce(string text, bool playNow = false)
    {
        if(_configuration.GetValue<bool>("DigitalEye:TextToSpeech:Enabled") && _speaker != null)
        {
            await _speaker.SayAsync(text, playNow, false);
        }
        else
        {
            _logger.LogInformation($"[Assistant: Speaking]: {text}");        
        }
    }
    #endregion
}
