using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using DigitalEye.Helpers;

namespace DigitalEye.Services;

public partial class RouterService : IDisposable
{
    private readonly ILogger<RouterService> _logger;
    public Speaker _speaker;
    private readonly HttpClient _httpClient;
    private readonly string _modelName;
    private readonly string _ollamaUrl;

    public RouterService(string modelPath, ILogger<RouterService> logger, Speaker speaker, IConfiguration config)
    {
        _logger=logger;
        _speaker = speaker;
        _modelName = config["DigitalEye:LLM:Model"] ?? "qwen2.5:0.5b";
        _ollamaUrl = config["DigitalEye:LLM:Url"] ?? "http://localhost:11434/api/chat";
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15) // Don't let it hang forever
        };
    }

    public async Task WarmUpAsync()
    {
        _logger.LogInformation("Warming up RouterService...");
        var result = await RouteCommandAsync("This is a test command to ensure you are awake.", 60);
        if (result.Intent == CommandIntent.Error)
        {
            _logger.LogWarning("RouterService warm-up failed.");
        }
        else
        {
            _logger.LogInformation("RouterService warm-up successful.");
        }
    }

    public async Task<RouteCommandResult> RouteCommandAsync(string transcript, int timeout = 5)
    {
        try
        {
            var systemPrompt = """
            You are the brain of a wearable device.
            Analyze the user's spoken command and map it to a JSON object.
            
            Valid Intents:
            - IDENTIFY: User wants to see/read/describe the environment (e.g. "read text", "what is this", "describe scene").
            - SYSTEM: User wants to change settings (e.g. "volume up", "shutdown", "battery").
            - OVERRIDE: User wants to control audio flow (e.g. "stop", "pause", "skip").
            
            Valid Payloads:
            - For SYSTEM: "volume_up", "volume_down", "volume_set <number>", "mute", "unmute", "shutdown".
            - For IDENTIFY: Respond with the input payload. This will be passed to another recognizer.
            - For OVERRIDE: "stop", "pause", "skip", "play".

            Example Input: "Make it louder"
            Example JSON: { "intent": "SYSTEM", "payload": "volume_up" }
            
            Example Input: "Read this sign"
            Example JSON: { "intent": "IDENTIFY", "payload": "Read this sign" }

            Output ONLY valid JSON.
            """;

            var requestBody = new
            {
                model = _modelName,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = transcript }
                },
                stream = false,
                format = "json", // CRITICAL: Forces Ollama to output valid JSON
                options = new
                {
                    temperature = 0.1, // Be logical, not creative
                    num_predict = 64   // Keep response short
                }
            };

            var jsonContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(_ollamaUrl, jsonContent);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Ollama API Error: {response.StatusCode}");
                return new RouteCommandResult(CommandIntent.Error, new ErrorPayload("API Error"));
            }

            var responseString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseString);

            // Ollama returns the text inside a "message.content" field
            var contentJson = doc.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            // 4. Deserialize the inner JSON
            var result = JsonSerializer.Deserialize<RouterResponse>(contentJson);

            // 5. Map string to your Enum
            if (Enum.TryParse<CommandIntent>(result.Intent, true, out var intentEnum))
            {
                return new RouteCommandResult(intentEnum, intentEnum switch
                {
                    CommandIntent.System => new SystemPayload(Enum.Parse<SystemCommand>(result.Payload, true)),
                    CommandIntent.Override => new OverridePayload(Enum.Parse<OverrideCommand>(result.Payload, true)),
                    CommandIntent.Identify => new IdentifyPayload(result.Payload),
                    _ => new ErrorPayload("Unknown intent")
                });                
            }

            return new RouteCommandResult(CommandIntent.Error, new ErrorPayload("Invalid Intent"));            
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in RouterService: {ex.Message}");
            return new RouteCommandResult(CommandIntent.Error, new ErrorPayload("An error occurred while processing your command."));
        }        
    }

    public void Dispose() => _httpClient.Dispose();
}
