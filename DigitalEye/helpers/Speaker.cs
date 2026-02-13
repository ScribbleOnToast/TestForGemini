namespace DigitalEye.Helpers;

using SherpaOnnx;
using System.Diagnostics;
using System.IO;

public class Speaker : IDisposable
{
    private readonly OfflineTts _tts;
    private readonly int _sampleRate = 22050; // Piper models are usually 22k
    private readonly int _voiceId;
    private readonly ILogger<Speaker> _logger;
    private LinkedList<string> _messageQueue = new();
    private Process? _currentPlaybackProcess;
    private readonly object _playbackLock = new object();
    private bool isPlaying = false;
    public Speaker(string modelDir, int voiceId, ILogger<Speaker> logger)
    { 
        _logger = logger;
        string modelPath = Path.Combine(modelDir, "en_US-amy-low.onnx");
        string tokensPath = Path.Combine(modelDir, "tokens.txt");
        string dataDir = Path.Combine(modelDir, "espeak-ng-data");        
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = modelPath;
        config.Model.Vits.Tokens = tokensPath;
        config.Model.Vits.DataDir = dataDir;    
        config.Model.NumThreads = 1;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
        _voiceId = voiceId;
        _sampleRate = _tts.SampleRate;
        Task.Run(() => ProcessQueueAsync(CancellationToken.None));
    }

    public async Task SayAsync(string text, bool playNow = false, bool sayNext = false)
    {
        if (playNow)
        {
            await SayInternalAsync(text, true);
        }
        else if(sayNext)
        {
            _messageQueue.AddFirst(text);
        }
        else
        {
            _messageQueue.AddLast(text);
        }
    }

    public async Task StopSpeaking(bool stopQueue = true)
    {
        try
        {
        lock (_playbackLock)
        {
            if (_currentPlaybackProcess != null)
            {
                try
                {
                    _messageQueue.Clear();
                    _currentPlaybackProcess.Kill();
                    _currentPlaybackProcess.Dispose();
                    _currentPlaybackProcess = null;
                    isPlaying = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to kill existing audio: {ex.Message}");
                }
            }
        }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error stopping audio: {ex.Message}");
        }
    }

    public async Task PlayBeep(string type)
    {
        string beepFile = string.Empty;
        switch(type.ToLowerInvariant())
        {
            case "confirmation":
                beepFile = "model/audio/confirm.mp3";
                break;
            case "error":
                beepFile = "model/audio/error.mp3";
                break;
        }
        var info = new ProcessStartInfo
        {
            FileName = "paplay",
            Arguments = $"{beepFile}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
    
        using var speakerProcess = Process.Start(info);       
    }

    public async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!isPlaying && _messageQueue.Count > 0)
            {
                string message = _messageQueue.FirstOrDefault() ?? string.Empty;
                _messageQueue.RemoveFirst();
                _ = SayInternalAsync(message, false);
            }
            else
            {
                await Task.Delay(100); // Avoid busy waiting
            }
        }
    }

    private async Task SayInternalAsync(string text, bool isImmediate = false)
    {
        isPlaying = true;
        if (string.IsNullOrWhiteSpace(text)) return;
        if(isImmediate)
        {
            _logger.LogInformation($"[Speaking Override]");
            await StopSpeaking();
        }

        // 1. Generate Audio (Returns float array)
        var audio = _tts.Generate(text: text, speed: 1.0f, speakerId: _voiceId);
        
        // 2. Save to temporary WAV file
        string tempFile = Path.GetTempFileName() + ".wav";
        SaveWav(tempFile, audio.Samples, _sampleRate);

        // 3. Play via PulseAudio (paplay)
        // We use 'paplay' because it handles mixing with your mic input process gracefully
        var info = new ProcessStartInfo
        {
            FileName = "paplay",
            Arguments = $"--property=media.role=announce {tempFile}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
    
        try 
        {
        var speakerProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "aplay",
                    Arguments = $"-q \"{tempFile}\"",
                    RedirectStandardOutput = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                // CRITICAL FIX 1: This is required for .Exited to fire!
                EnableRaisingEvents = true 
            };

            // CRITICAL FIX 2: Subscribe BEFORE starting
            speakerProcess.Exited += (sender, args) =>
            {
                _logger.LogInformation("[Assistant: Speaking Finished]");
                isPlaying = false;
                // Cleanup logic...
                try { File.Delete(tempFile); } catch { }
                speakerProcess.Dispose();
            };

            lock (_playbackLock)
            {
                _currentPlaybackProcess = speakerProcess;
            }

            // Now it is safe to start
            speakerProcess.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Playback error: {ex.Message}");
        }
    }

    private void SaveWav(string filename, float[] samples, int sampleRate)
    {
        using var stream = new FileStream(filename, FileMode.Create);
        using var writer = new BinaryWriter(stream);

        // RIFF header
        writer.Write("RIFF".ToCharArray());
        writer.Write(36 + samples.Length * 2); // File size
        writer.Write("WAVE".ToCharArray());

        // fmt chunk
        writer.Write("fmt ".ToCharArray());
        writer.Write(16); // Chunk size
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2); // Byte rate
        writer.Write((short)2); // Block align
        writer.Write((short)16); // Bits per sample

        // data chunk
        writer.Write("data".ToCharArray());
        writer.Write(samples.Length * 2);

        // Convert float [-1.0, 1.0] to short [−32768, 32767]
        foreach (var sample in samples)
        {
            short s = (short)(Math.Clamp(sample, -1.0f, 1.0f) * 32767);
            writer.Write(s);
        }
    }

    public void Dispose()
    {
        _tts?.Dispose();
    }
}