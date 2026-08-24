using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Pulsar.Common;

/// <summary>
/// Stops the process once the game process exits. The plugin normally kills subprocesses
/// on unload. This covers the cases where it doesn't, like the game crashing.
/// </summary>
public sealed class ParentWatchdog(int parentProcessId, IHostApplicationLifetime lifetime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = WatchAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WatchAsync()
    {
        try
        {
            using var parent = Process.GetProcessById(parentProcessId);
            await Task.Run(parent.WaitForExit).WaitAsync(lifetime.ApplicationStopping);
            Log.Information("Parent process {pid} exited; shutting down", parentProcessId);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Information(ex, "Parent process {pid} is not observable; shutting down", parentProcessId);
        }

        lifetime.StopApplication();
    }
}
