namespace DigitalEye.Helpers;

using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Logging;

/// <summary>
/// Helper Class for connecting and configuring Bluetooth audio input devices on Linux (PipeWire/BlueZ).
/// This class is designed to be registered with DI and receive an `ILogger&lt;BluetoothAudioGuard&gt;`.
/// </summary>
public class BluetoothAudioGuard : IBluetoothAudioGuard
{   
    private readonly ILogger<BluetoothAudioGuard> _logger;

    public BluetoothAudioGuard(ILogger<BluetoothAudioGuard> logger)
    {
        _logger = logger;
    }

    // Can't use normal BT audio profiles for recording (they are designed for output), so we need to force the "Headset" profile which supports Mic input. 
    // The audio profile we WANT (Standard HFP/HSP which supports Mic)
    // In PipeWire, this is usually just "headset-head-unit"
    private static readonly string[] VoiceProfiles = { "headset-head-unit", "handsfree-head-unit" };
    
    // Codecs we know are "Unstable" for input on Pi5/PipeWire currently
    private static readonly string[] UnstableCodecs = {};//{ "lc3", "lc3-swb" };

    /// <summary>
    /// To defeat power-saving features in PipeWire that can cause the Bluetooth connection to drop after a period of inactivity, we start a "Shadow Monitor" process.
    /// This process continuously reads from the Bluetooth Speaker Monitor (which is active whenever the Bluetooth card is active) and discards the data. By doing this, 
    /// we keep the Bluetooth connection alive and ensure that the microphone remains active and ready to use at all times. This is a common workaround for
    /// </summary>
    private Process? _shadowMonitor;

    /// <summary>
    /// Main method to ensure Bluetooth audio input is ready. This includes:
    /// 1. Detecting the Bluetooth audio card
    /// 2. Ensuring the correct voice profile is active (not the default "Music" profile)
    /// 3. Mitigating known unstable codec issues by forcing a renegotiation if an unstable codec is detected
    /// 4. Verifying that an input source is created and setting it as the default microphone
    /// </summary>
    /// <returns></returns>
    public string? EnsureInputReady()
    {
        _logger.LogInformation("[AudioGuard] Scanning for Bluetooth audio hardware...");

        // 1. Find the Bluetooth Card
        var cardName = FindBluetoothCard();
        if (string.IsNullOrEmpty(cardName))
        {
            _logger.LogCritical("[AudioGuard] CRITICAL: No Bluetooth Audio Card detected.");
            return null;
        }

        _logger.LogInformation("[AudioGuard] Found Bluetooth Card: {card}", cardName);

        // 2. Check and Fix Profile (Switch from Music -> Call mode)
        if (!EnsureVoiceProfile(cardName))
        {
            _logger.LogError("[AudioGuard] Failed to set Voice Profile.");
            return null;
        }

        // 3. Check for "Bleeding Edge" Codec Issues (LC3)
        CheckAndMitigateUnstableCodec(cardName);

        // 4. Final Verification: Do we have an Input Source?
        if (HasInputSource())
        {
            // Capture the source name logic here
            var targetSource = GetMicSourceName(cardName);
            if (targetSource != null)
            {
                _logger.LogInformation("[AudioGuard] Found Real Mic: {source}", targetSource);
                EnsureVolumeIsUnmuted(targetSource);
                //StartShadowMonitor(cardName);
                RunShell("pactl", $"set-default-source {targetSource}");

                return targetSource; 
            }
        }

        _logger.LogWarning("[AudioGuard] FAILURE: Bluetooth card is present, but no Input Source created.");
        return null;
    }

    // Extract the finding logic to a helper
    private string? GetMicSourceName(string cardName)
    {
        var output = RunShell("pactl", "list sources short");
        var idPart = cardName.Replace("bluez_card.", "");
        
        foreach (var line in output.Split('\n'))
        {
            if (line.Replace(":","_").Contains(idPart) && !line.Contains(".monitor"))
            {
                var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return parts[1];
            }
        }
        return null;
    }

    private void EnsureVolumeIsUnmuted(string sourceName)
    {
        _logger.LogInformation("[AudioGuard] Forcing un-mute and 100% volume on {source}", sourceName);

        // 1. Unmute
        RunShell("pactl", $"set-source-mute {sourceName} 0");

        // 2. Set Volume to 100% (65536 is 100% in some contexts, but '100%' works in pactl)
        RunShell("pactl", $"set-source-volume {sourceName} 110%");
    }

    /// <summary>
    /// Find the Bluetooth audio card using 'pactl list cards' 
    /// and return its name. We look for any card that uses the "bluez" module, 
    /// which indicates it's a Bluetooth audio device.
    /// </summary>
    /// <returns></returns>
    private string? FindBluetoothCard()
    {
        // We look for any card using the bluez module
        var output = RunShell("pactl", "list cards short");        
        var lines = output.Split('\n');
        foreach (var line in lines)
        {
            if (line.Contains("bluez_card"))
            {
                var match = Regex.Match(line, @"(bluez_card\.[a-zA-Z0-9_]+)");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        return null;
    }

    /// <summary>
    /// try to force the Bluetooth card into a voice profile (HFP/HSP) which supports microphone input. 
    /// </summary>
    /// <param name="cardName"></param>
    /// <returns></returns>
    private bool EnsureVoiceProfile(string cardName)
    {
        var info = GetCardInfo(cardName);
        _logger.LogInformation("[AudioGuard.EnsureVoiceProfile] Status - Profile: {profile} | Codec: {codec}", info.Profile, info.Codec);

        // If we are already on a voice profile, we are good.
        if (VoiceProfiles.Any(vp => info.Profile.Contains(vp))) return true;

        // Otherwise, FORCE the switch
        _logger.LogWarning("[AudioGuard] Wrong profile detected ({profile}). Switching to headset-head-unit...", info.Profile);
        RunShell("pactl", $"set-card-profile {cardName} headset-head-unit");
        
        // Wait for Bluetooth handshake
        Thread.Sleep(2500);

        // Verify the switch worked
        info = GetCardInfo(cardName);
        if (VoiceProfiles.Any(vp => info.Profile.Contains(vp))) return true;
        _logger.LogError("[AudioGuard] Failed to set voice profile.");
        return false;
    }

    /// <summary>
    /// Some Bluetooth codecs (notably LC3 on PipeWire) can be unstable for input and cause the stream to fail after a few seconds.
    /// </summary>
    /// <param name="cardName"></param>
    private void CheckAndMitigateUnstableCodec(string cardName)
    {
        var info = GetCardInfo(cardName);

        foreach (var badCodec in UnstableCodecs)
        {
            if (info.Codec.ToLower().Contains(badCodec))
            {
                _logger.LogWarning("[AudioGuard] WARNING: Unstable codec detected ({codec}). Attempting to downgrade...", info.Codec);
                RunShell("pactl", $"set-card-profile {cardName} headset-head-unit");
                Thread.Sleep(2000);
                
                var newInfo = GetCardInfo(cardName);
                _logger.LogInformation("[AudioGuard] Codec after reset: {codec}", newInfo.Codec);
                break;
            }
        }
    }

    private (string Profile, string Codec) GetCardInfo(string cardName)
    {
        // We need full output to parse properties
        var output = RunShell("pactl", $"list cards");
        
        // Isolate the block for our card
        var cardBlock = "";
        var blocks = output.Split(["Card #"], StringSplitOptions.RemoveEmptyEntries);
        foreach(var b in blocks)
        {
            if(b.Contains(cardName)) 
            {
                cardBlock = b; 
                break; 
            }
        }

        if (string.IsNullOrEmpty(cardBlock)) return ("unknown", "unknown");

        // 1. Parse Active Profile Name
        var profileMatch = Regex.Match(cardBlock, @"Active Profile:\s+([^\n\r]+)");
        string profile = profileMatch.Success ? profileMatch.Groups[1].Value.Trim() : "unknown";

        string codec = "unknown";

        if (profile != "unknown" && profile != "off")
        {
            // 2. Find the definition line for this specific profile
            // Look for: tab, tab, profileName, colon, anything, "codec", space, (capture value)
            // We use Regex.Escape(profile) to ensure special chars in the name don't break the regex
            var codecPattern = $@"\s+{Regex.Escape(profile)}:.*?codec\s+([^\)\s,]+)";
            
            var codecMatch = Regex.Match(cardBlock, codecPattern, RegexOptions.IgnoreCase);
            if (codecMatch.Success)
            {
                codec = codecMatch.Groups[1].Value.Trim();
            }
        }
        return (profile, codec);
    }

    private bool HasInputSource()
    {
        // Check short source list for any bluez input
        var output = RunShell("pactl", "list sources short");
        return output.Contains("bluez_input");
    }

    private static string RunShell(string cmd, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            string res = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return res;
        }
        catch (Exception)
        {
            // If pactl is missing or fails hard
            return "";
        }
    }

    public void StartShadowMonitor(string cardName)
    {
        _logger.LogInformation("[AudioGuard] Starting Shadow Monitor (The 'Pavucontrol' Fix)...");

        // 1. Calculate the Monitor Name
        // Card: bluez_card.AC_3E...
        // Target: bluez_output.AC_3E...1.monitor
        var idPart = cardName.Replace("bluez_card.", "");
        var monitorName = $"bluez_output.{idPart}.1.monitor";

        // 2. Kill existing if running
        if (_shadowMonitor != null && !_shadowMonitor.HasExited)
        {
            try { _shadowMonitor.Kill(); } catch {}
        }

        // 3. Spawn PAREC (Not pw-record)
        // parecc supports the '--property' flag which is critical for setting "stream.is-live"
        var psi = new ProcessStartInfo
        {
            FileName = "parec",
            // ARGUMENTS EXPLAINED:
            // --device: Connect to the Speaker Monitor (forces Output clock to run)
            // --latency-msec=20: Fast polling (Keep Alive)
            // --property=media.role=phone: Tells BlueZ "This is a call" (Never Sleep)
            // --property=stream.is-live=true: Tells PipeWire "Realtime Data" (No Suspend)
            // /dev/null: Dump the data, we don't need it.
            Arguments = $"--device={monitorName} --format=s16le --rate=16000 --channels=1 --latency-msec=20 --property=media.role=phone --property=stream.is-live=true /dev/null",
            RedirectStandardOutput = false, 
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try 
        {
            _shadowMonitor = Process.Start(psi);
            _logger.LogInformation("[AudioGuard] Shadow Monitor active on: {monitor}", monitorName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AudioGuard] WARNING: Failed to start shadow monitor: {msg}", ex.Message);
        }
    }

    public void StopShadowMonitor()
    {
        if (_shadowMonitor != null && !_shadowMonitor.HasExited)
        {
            try 
            { 
                _logger.LogInformation("[AudioGuard] Stopping Shadow Monitor...");
                _shadowMonitor.Kill(); 
            } 
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AudioGuard] WARNING: Failed to stop shadow monitor: {msg}", ex.Message);
            }
        }
    }
}