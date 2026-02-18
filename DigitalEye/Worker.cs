namespace DigitalEye;

using DigitalEye.Helpers;
using System.Diagnostics;
using SherpaOnnx;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Net.Sockets;
using System.Text.Json;
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
    //VLM backend process and socket (I talk to the camera)
    private Process? _vlmProcess;
    private Socket? _vlmSocket;
    private const string VLMSocketPath = "/tmp/digitaleye_vision.sock";
    private const string VLMPythonScript = "vision_interface.py";
    private bool _vlmReady = false;

    //LLM backend process and socket (I talk to the language engine - currently in-process, but could be separate)
    private Process? _llmProcess;
    private Socket? _llmSocket;
    private const string LLMSocketPath = "/tmp/digitaleye_brain.sock";
    private const string LLMPythonScript = "brain_engine.py";
    private bool _llmReady = false;

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
        
        _ = Task.Run(async () =>
        {            
            while (!_vlmReady && !_llmReady)
            {
                await Task.Delay(3000, stoppingToken);
                _ = _speaker.PlayBeep("confirmation");
            }            
        });

        await StartVLMEngine(stoppingToken);
        await StartLLMEngine(stoppingToken);
        _ = StartListeningForBrainResponsesAsync(stoppingToken);
        await StartListeningLoopAsync(micDevice, stoppingToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Cleaning up worker resources.");
        _llmSocket?.Close();
        _vlmSocket?.Close();
        _vlmProcess?.Kill(entireProcessTree: true);
        _llmProcess?.Kill(entireProcessTree: true);
        _recognizer?.Dispose();
        _recognizer = null;
        _audioGuard.StopShadowMonitor();
        return base.StopAsync(cancellationToken);
    }
    #endregion

#region init routines
    private async Task StartVLMEngine(CancellationToken ct)
    {
        await Announce("Starting Camera System. This may take 20 to 30 seconds.");
        File.Delete(VLMSocketPath); // Clean up old socket if exists

        // Start the venv python directly (avoids an extra shell and makes shutdown predictable)
        //var venvPath = Path.Combine(AppContext.BaseDirectory, "venv");
        //dev
        var venvPath = "/opt/digitaleye/venv";       
        
        var pythonExe = System.IO.Path.Combine(venvPath, "bin", "python");
        var start = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"-u {VLMPythonScript}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = "./pyscripts"
        };
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        start.Environment["VIRTUAL_ENV"] = venvPath;
        start.Environment["PATH"] = System.IO.Path.Combine(venvPath, "bin") + ":" + existingPath;
        _vlmProcess = new Process { StartInfo = start };

        // Pipe Python logs to C# Logger
        _vlmProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogDebug($"[VLM LOG OD]: {e.Data}"); };
        _vlmProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Libcamera and HailoRT are noisy on stderr. 
                // If it says "INFO" or "WARN", just log it as info/warning, not an error.
                if (e.Data.Contains("INFO") || e.Data.Contains("WARN"))
                {
                    _logger.LogDebug($"[VLM LOG ER]: {e.Data}");
                }
                else
                {
                    // Real errors (tracebacks, crashes, critical failures)
                    _logger.LogError($"[VLM ERR]: {e.Data}");
                }
            }
        };

        
        //Start the python process for the vision identification system
        _vlmProcess.Start();
        _vlmProcess.BeginOutputReadLine();
        _vlmProcess.BeginErrorReadLine();
        _logger.LogDebug($"DigitalEye Brain started (PID: {_vlmProcess.Id})");

        //Wait for python script to create the socket file before trying to connect
        while (!File.Exists(VLMSocketPath) && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Brain Socket not found yet, waiting...");
            await Task.Delay(1000, ct);
        }

        _logger.LogDebug("Connecting to unix socket socket...{SocketPath}", VLMSocketPath);
        _vlmSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(VLMSocketPath);

        int attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _vlmSocket.ConnectAsync(endpoint, ct);
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

        //Wait for ready signal from python interface before accepting commands
        while (!ct.IsCancellationRequested && !_vlmReady)
        {
            _logger.LogDebug("Waiting for Brain to signal ready...");
            if (_vlmSocket != null && _vlmSocket.Connected)
            {
                var buffer = new byte[4096];
                var bytesRead = await _vlmSocket.ReceiveAsync(buffer, SocketFlags.None);
                var jsonRes = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                // C. Parse
                var doc = JsonSerializer.Deserialize<VisionResponse>(jsonRes);
                if (doc!.Type == "ready")
                {
                    _logger.LogDebug("python interface and backend are ready!");
                    _vlmReady = true;
                }
            }
            await Task.Delay(3000, ct);
        }
        await Announce("Camera System is ready.", playNow: true);
    }

    private async Task StartLLMEngine(CancellationToken ct)
    {
        await Announce("Starting Language Engine...", false);
        File.Delete(LLMSocketPath); // Clean up old socket if exists

        var venvPath = "/opt/digitaleye/venv";       
        
        var pythonExe = Path.Combine(venvPath, "bin", "python");
        var start = new ProcessStartInfo
        {
            FileName = pythonExe,
            Arguments = $"-u {LLMPythonScript}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = "./pyscripts"
        };

        _llmProcess = new Process { StartInfo = start };

        // Pipe Python logs to C# Logger
        _llmProcess.OutputDataReceived += (s, e) => { if (e.Data != null) _logger.LogDebug($"[LLM LOG OD]: {e.Data}"); };
        _llmProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                // Libcamera and HailoRT are noisy on stderr. 
                // If it says "INFO" or "WARN", just log it as info/warning, not an error.
                if (e.Data.Contains("INFO") || e.Data.Contains("WARN"))
                {
                    _logger.LogDebug($"[LLM LOG ER]: {e.Data}");
                }
                else
                {
                    // Real errors (tracebacks, crashes, critical failures)
                    _logger.LogError($"[LLM ERR]: {e.Data}");
                }
            }
        };

        _llmProcess.Start();
        _llmProcess.BeginOutputReadLine();
        _llmProcess.BeginErrorReadLine();
        _logger.LogDebug($"LLM Interface started (PID: {_llmProcess.Id})");

        while (!File.Exists(LLMSocketPath) && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("LLM Socket not found yet, waiting...");
            await Task.Delay(1000, ct);
        }

        _logger.LogDebug("Connecting to unix socket socket...{SocketPath}", LLMSocketPath);
        _llmSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(LLMSocketPath);

        int attempts = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _llmSocket.ConnectAsync(endpoint, ct);
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
        await _routerService.WarmUpAsync();
        await Announce("LLM Interface is ready.", playNow: true);
        _llmReady = true;
    }
    
    public async Task StopVLMEngine(CancellationToken ct)
    {
        try
        {
            _ = Announce("Stopping Camera System.");
            if (_vlmProcess != null && !_vlmProcess.HasExited)
            {
                try
                {
                    _vlmProcess.Close();
                    if (!_vlmProcess.WaitForExit(5000))
                    {
                        _logger.LogWarning("Python Brain did not exit gracefully. Attempting to kill...");
                        _vlmProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)            {
                    _logger.LogError(ex, "Error while stopping Python Brain: {Message}", ex.Message);
                    try { _vlmProcess.Kill(); } catch { }
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
            _ = Announce("Camera System stopped.");
            _vlmProcess = null;
            _vlmSocket?.Close();    
            _vlmSocket = null;  
        }
    }

    public async Task StopLLMEngine(CancellationToken ct)
    {
        try
        {
            _ = Announce("Stopping Language System.");
            if (_llmProcess != null && !_llmProcess.HasExited)
            {
                try
                {
                    _llmProcess.Close();
                    if (!_llmProcess.WaitForExit(5000))
                    {
                        _logger.LogWarning("Python Brain did not exit gracefully. Attempting to kill...");
                        _llmProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception ex)            {
                    _logger.LogError(ex, "Error while stopping Python Brain: {Message}", ex.Message);
                    try { _llmProcess.Kill(); } catch { }
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
            _ = Announce("Camera System stopped.");
            _llmProcess = null;
            _llmSocket?.Close();    
            _llmSocket = null;  
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
                            _speaker.ChangeVolume("up", sysPayload.Value ?? 10);
                            break;
                        case SystemCommand.Volume_Down:
                            _speaker.ChangeVolume("down", sysPayload.Value ?? 10);
                            break;
                        case SystemCommand.Volume_Set:
                            _speaker.ChangeVolume(string.Empty, sysPayload.Value ?? 10);
                            break;
                        case SystemCommand.Mute:
                            _speaker.Mute(true);
                            break;
                        case SystemCommand.Unmute:
                            _speaker.Mute(false);
                            break;
                        case SystemCommand.Shutdown:
                            await StopVLMEngine(cancellationToken);
                            Environment.Exit(0);
                            break;
                    }
                }
                break;
            case CommandIntent.Override:
                var overridePayload = cmd.Payload as OverridePayload;
                _ = Announce($"Executing override command: {overridePayload.Action}");
                // Here you would add logic to handle each override command, e.g.:
                switch (overridePayload.Action)          
                {
                    case OverrideCommand.Stop:
                    case OverrideCommand.Skip:
                        await _speaker.StopSpeaking(overridePayload.Action == OverrideCommand.Stop);
                        break;
                    case OverrideCommand.Pause:
                    case OverrideCommand.Play:
                        // Pause audio logic
                        await _speaker.PauseSpeaking(overridePayload.Action == OverrideCommand.Pause);
                        break;
                    default:
                        _logger.LogWarning($"Unknown override command received: {overridePayload.Action}");
                        break;
                }
                break;       
        }
    }

    public async Task GetVLMInference(string prompt = "Describe the scene")
    {
        try
        {
            _logger.LogDebug("Getting VLM response: {Prompt}", prompt);
            if (_vlmSocket == null || !_vlmSocket.Connected)
            {
                _logger.LogError("Error: VLM Offline");
                await Announce("Error: VLM Offline");
                return;
            }
            var jsonReq = JsonSerializer.Serialize(prompt) + "\n"; // newline-delimited
            var reqBytes = Encoding.UTF8.GetBytes(jsonReq);
            int bytesSent = await _vlmSocket.SendAsync(new ArraySegment<byte>(reqBytes), SocketFlags.None);
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
            if (_llmSocket == null) return;
            var messageBuffer = new StringBuilder();
            var buffer = new byte[4096];

            while (!stoppingToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await _llmSocket.ReceiveAsync(new ArraySegment<byte>(buffer), SocketFlags.None);
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
