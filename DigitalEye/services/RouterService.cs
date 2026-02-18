using DigitalEye.Helpers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DigitalEye.Services;

public class RouterService : IDisposable
{
    private readonly ILogger<RouterService> _logger;
    public Speaker _speaker;

    public RouterService(string modelPath, ILogger<RouterService> logger, Speaker speaker)
    {
        _logger = logger;
        _speaker = speaker;
    }

    public async Task WarmUpAsync()
    {
        _logger.LogInformation("Warming up local LLM...");
        // Run a dummy inference to load weights into memory
        await RouteCommandAsync("Hello", 10); 
        _logger.LogInformation("Warm-up complete.");
    }

    public async Task<RouteCommandResult> RouteCommandAsync(string transcript, int timeout = 5)
    {
        return await RunInference(transcript);
    }

    private async Task<RouteCommandResult> RunInference(string transcript)
    {
        try
        {
            var socketPath = "/tmp/digital_eye_brain.sock";
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));

            // Send the transcription from Sherpa
            byte[] data = Encoding.UTF8.GetBytes(transcript);
            await socket.SendAsync(data, SocketFlags.None);

            // Receive the JSON result
            byte[] buffer = new byte[1024];
            int received = await socket.ReceiveAsync(buffer, SocketFlags.None);
            var response = JsonSerializer.Deserialize<LLMResponse>(Encoding.UTF8.GetString(buffer, 0, received));
            var intent = response?.Intent.ToLowerInvariant();
            return intent switch
            {
                "system" => new RouteCommandResult(CommandIntent.System, ParseSystemPayload(response.Payload)),
                "override" => new RouteCommandResult(CommandIntent.Override, ParseOverridePayload(response.Payload)),
                _ => new RouteCommandResult(CommandIntent.Error, new ErrorPayload($"Unknown Intent: {response?.Intent}"))
            };                        
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference Failed");
            return new RouteCommandResult(CommandIntent.Error, new ErrorPayload("Brain Error"));
        }
    }

    private SystemPayload ParseSystemPayload(string rawPayload)
    {
        try 
        {
            var parts = rawPayload.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var cmdString = parts[0];
            
            if (Enum.TryParse<SystemCommand>(cmdString, true, out var command))
            {
                int? val = null;
                if (parts.Length > 1 && int.TryParse(parts[1], out int v)) val = v;
                return new SystemPayload(command, val);
            }
        }
        catch { /* Ignore parsing errors, fall through */ }
        return new SystemPayload(SystemCommand.Volume_Up); // Default fallback
    }

    private CommandPayload ParseOverridePayload(string rawPayload)
    {
        if (Enum.TryParse<OverrideCommand>(rawPayload.Trim(), true, out var cmd))
            return new OverridePayload(cmd);
        return new ErrorPayload("Invalid Override Command");
    }

    public void Dispose()
    {

    }
}