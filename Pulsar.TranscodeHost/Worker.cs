using Pulsar.Common;
using Pulsar.Common.Api;

namespace Pulsar.TranscodeHost;

public class Worker(ILogger<Worker> logger, HostArgs args) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
            try
            {
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("TranscodeHost running at: {time}", DateTimeOffset.Now);

                await RpcServer.NamedPipeServerAsync<PrepareServer>(args.PipeName ?? PipeNames.TranscodeHost, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RPC server crashed; restarting in 1s");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
    }
}
