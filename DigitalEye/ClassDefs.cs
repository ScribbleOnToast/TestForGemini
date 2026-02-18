using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace DigitalEye
{    
    public class VisionResponse
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public PayloadWrapper Payload { get; set; } = new();
        public class PayloadWrapper
        {
            [JsonPropertyName("answer")]
            public string Answer { get; set; } = string.Empty;

            [JsonPropertyName("time")]
            public string Latency { get; set; } = string.Empty;
        }
    }

    public class LLMResponse
    {
        [JsonPropertyName("intent")]
        public string Intent { get; set; } = "ERROR";

        [JsonPropertyName("payload")]
        public string Payload { get; set; } = "";
    }
    public enum CommandIntent
    {
        Identify, // Pass the command straight to the vision service for processing
        Override, // Commands that interact with currently playihng audio, such as "stop" or "skip" or "pause"
        System, // Commands that control the system, like volume or shutdown
        Error // Commands that could not be processed
    }

    public record RouteCommandResult(CommandIntent Intent, CommandPayload Payload);
    public abstract record CommandPayload;
    public record SystemPayload(SystemCommand Action, int? Value = null) : CommandPayload;
    public record OverridePayload(OverrideCommand Action) : CommandPayload;
    public record IdentifyPayload(string RawInput) : CommandPayload; // Just the raw text
    public record ErrorPayload(string Message) : CommandPayload;

    public enum SystemCommand
    {
        // System commands
        Volume_Up,
        Volume_Down,
        Volume_Set, // Requires a number payload
        Mute,
        Unmute,
        Shutdown
    }
    
    public enum OverrideCommand
    {
        // Override commands    
        Stop,
        Pause,
        Skip,
        Play,
    }
}
