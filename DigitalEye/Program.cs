using DigitalEye;
using DigitalEye.Helpers;
using DigitalEye.Services;
using Serilog;
using Serilog.Core;
using System.Runtime.InteropServices;

Console.WriteLine("Application Init");
string logTemplate = "[{Timestamp:HH:mm:ss} {Level:u4}] <{SourceContext}> {Message:lj}{NewLine}{Exception}";
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate: logTemplate)
    .WriteTo.File("logs/digitaleye-.log",
        rollingInterval: RollingInterval.Hour,
        outputTemplate: logTemplate)
    .CreateLogger();
Log.Logger.Information("Logger initialized");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.Services.AddSingleton(sp => 
{
    var logger = sp.GetRequiredService<ILogger<Speaker>>();
    var config = sp.GetRequiredService<IConfiguration>();
    var modelPath = Path.Combine("model/tts/", config.GetValue<string>("DigitalEye:TextToSpeech:VoiceModel") ?? "vits-piper-en_US-amy-low");
    var voiceId = config.GetValue<int>("DigitalEye:TextToSpeech:VoiceId");
    return new Speaker(modelPath, voiceId, logger);
});

builder.Services.AddSingleton(sp =>
{
    var modelPath = Path.Combine("model/llm/", sp.GetRequiredService<IConfiguration>().GetValue<string>("DigitalEye:LLM:ModelPath") ?? "ggml-alpaca-7b-q4.bin");
    var logger = sp.GetRequiredService<ILogger<RouterService>>();
    var speaker = sp.GetRequiredService<Speaker>();
    var config = sp.GetRequiredService<IConfiguration>();
    return new RouterService(modelPath, logger, speaker, config);
});

builder.Services.AddSingleton<IBluetoothAudioGuard, BluetoothAudioGuard>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
try
{
    host.Run();
}
catch (Exception ex)
{
    Console.WriteLine($"Application terminated unexpectedly {ex}");
}
finally
{
    Log.CloseAndFlush();
}