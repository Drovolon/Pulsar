using Pulsar.Common;
using Pulsar.TranscodeHost;

using Serilog;

var hostArgs = HostArgs.Parse(args);

Log.Logger = new LoggerConfiguration()
             .MinimumLevel.Debug()
             .WriteTo.File(
                 path: Path.Combine(hostArgs.LogDirectory, $"{hostArgs.LogTag ?? "transcode"}-.log"),
                 rollingInterval: RollingInterval.Day,
                 buffered: false,
                 flushToDiskInterval: TimeSpan.FromSeconds(1),
                 outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
             .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSingleton(hostArgs);
    builder.Services.AddHostedService<Worker>();
    builder.Services.AddSerilog();

    // Self-terminate the process if the parent dies (game crashes, etc.)
    if (hostArgs.ParentProcessId is { } parentPid)
        builder.Services.AddHostedService(sp =>
            new ParentWatchdog(parentPid, sp.GetRequiredService<IHostApplicationLifetime>()));

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "TranscodeHost crashed");
}
finally
{
    Log.CloseAndFlush();
}
