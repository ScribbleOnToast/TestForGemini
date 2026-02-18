namespace DigitalEye.Helpers;

using SherpaOnnx;
using NetCoreAudio;
using System.Diagnostics;
using System.IO;

public class Speaker : IDisposable
{
    public int _currentVolume = 25;
    private readonly OfflineTts _tts;
    private readonly int _sampleRate = 22050; // Piper models are usually 22k
    private readonly int _voiceId;
    private readonly ILogger<Speaker> _logger;
    private LinkedList<string> _messageQueue = new();
    private Player player;
    public Speaker(string modelDir, int voiceId, ILogger<Speaker> logger)
    { 
        _logger = logger;      
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = Path.Combine(modelDir, "en_US-amy-low.onnx");;
        config.Model.Vits.Tokens = Path.Combine(modelDir, "tokens.txt");;
        config.Model.Vits.DataDir = Path.Combine(modelDir, "espeak-ng-data");    
        config.Model.NumThreads = 1;
        config.Model.Debug = 0;
        config.Model.Provider = "cpu";
        _tts = new OfflineTts(config);
        _voiceId = voiceId;
        _sampleRate = _tts.SampleRate;

        player = new Player();

        Task.Run(() => ProcessQueueAsync(CancellationToken.None));
    }

    #region Playback Controls (Stop/Pause/Skip)
    public async Task PauseSpeaking(bool pause)
    {        
        if(pause)
        {
            _logger.LogInformation("Pausing audio...");
            await player.Pause();
        }
        else
        {
            _logger.LogInformation("Resuming audio...");
            await player.Resume();
        }
    }

    public async Task StopSpeaking(bool stopQueue = true)
    {
        try
        {
            _logger.LogInformation("Stopping audio...");
            if(player.Playing)
                await player.Stop();
            if (stopQueue)
            {
                _logger.LogInformation("Clearing message queue...");
                _messageQueue.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error stopping audio: {ex.Message}");
        }
    }
    #endregion

    #region Queue Management
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
    public async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!player.Playing  && _messageQueue.Count > 0)
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

    #endregion

    #region Beep Sounds
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
    #endregion

    #region Internal TTS Logic
    private async Task SayInternalAsync(string text, bool isImmediate = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        var audio = _tts.Generate(text: text, speed: 1.0f, speakerId: _voiceId);
        
        // 2. Save to temporary WAV file
        string tempFile = Path.GetTempFileName() + ".wav";
        SaveWav(tempFile, audio.Samples, _sampleRate);    
        player.PlaybackFinished += (s, e) => 
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error deleting temp file: {ex.Message}");
            }
        };
        _ = player.Play(tempFile);
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
    #endregion
    
    #region Volume Control (Optional)
    public int ChangeVolume(string direction,int value = 10)
    {
        try
        {            
            int newVol = string.IsNullOrEmpty(direction) ? value :_currentVolume + (direction.ToLower() == "up" ? value : -value);
            newVol = Math.Clamp(newVol, 0, 100);
            _currentVolume = newVol;
            player.SetVolume((byte)_currentVolume);
            return _currentVolume;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error increasing volume: {ex.Message}");
            return _currentVolume;
        }
    }

    public bool Mute(bool mute)
    {
        try
        {
            player.SetVolume(mute ? (byte)0 : (byte)_currentVolume);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error toggling mute: {ex.Message}");
            return false;
        }
    }

    #endregion
    public void Dispose()
    {
        _tts?.Dispose();
    }
}