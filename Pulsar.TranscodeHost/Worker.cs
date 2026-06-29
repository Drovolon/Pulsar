using Pulsar.Common;
using Pulsar.Common.Api;

namespace Pulsar.TranscodeHost;

public class Worker(ILogger<Worker> logger, HostArgs args) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("TranscodeHost running at: {time}", DateTimeOffset.Now);
            }

            await RpcServer.NamedPipeServerAsync<PrepareServer>(args.PipeName ?? PipeNames.TranscodeHost, stoppingToken);
        }
    }
}
