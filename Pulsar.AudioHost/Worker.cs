using Pulsar.Common;
using Pulsar.Common.Api;

namespace Pulsar.AudioHost;

public class Worker(ILogger<Worker> logger, HostArgs args) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            }

            await RpcServer.NamedPipeServerAsync<AudioServer>(args.PipeName ?? PipeNames.AudioHost, stoppingToken);
        }
    }
}
