using System;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Ipc;
using Pulsar.Listening;

namespace Pulsar;

/// <summary>
/// Consumes outputs from ListeningManager and BroadcastManager, and routes
/// them to other interested parties in the application.
///
/// ListeningManager disables autoplay while broadcasting, for example.
/// Or, BgmMuter mutes in-game BGM while listening or broadcasting.
/// Broadcast outputs are routed into the IPC layer, as well as
/// the debug loopback layer.
/// </summary>
internal sealed class ApplicationCoordinator : IAsyncDisposable
{
    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly IpcProvider ipc;
    private readonly DebugLoopbackController debugLoopback;
    private readonly ListeningNotifier listeningNotifier;
    private readonly BgmMuter bgmMuter;
    private readonly Task broadcastOutputLoop;
    private readonly Task listeningOutputLoop;
    private BroadcastOutput.PlayerDataChanged? currentPublication;

    internal ApplicationCoordinator(
        BroadcastManager broadcast, ListeningManager listening, IpcProvider ipc, DebugLoopbackController debugLoopback,
        ListeningNotifier listeningNotifier, BgmMuter bgmMuter)
    {
        this.broadcast = broadcast;
        this.listening = listening;
        this.ipc = ipc;
        this.debugLoopback = debugLoopback;
        this.listeningNotifier = listeningNotifier;
        this.bgmMuter = bgmMuter;
        broadcastOutputLoop = RouteBroadcastOutputs();
        listeningOutputLoop = RouteListeningOutputs();
    }

    internal void RefreshBgmMute() => _ = bgmMuter.Refresh();

    private async Task RouteBroadcastOutputs()
    {
        try
        {
            await foreach (var output in broadcast.Outputs.ReadAllAsync())
                try
                {
                    switch (output)
                    {
                        case BroadcastOutput.PlayerDataChanged change:
                            var previous = currentPublication;
                            currentPublication = change;
                            try
                            {
                                await ipc.PublishPlayerDataAsync(change.Data);
                                await debugLoopback.OnPlayerDataChangedAsync(change.Data);
                            } finally
                            {
                                previous?.Dispose();
                            }

                            break;
                        case BroadcastOutput.BroadcastingChanged(var value):
                            listening.SetBroadcasting(value);
                            await bgmMuter.SetBroadcasting(value);
                            break;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.Error(e, "failed to route broadcast output: {output}", output);
                }
        } finally
        {
            currentPublication?.Dispose();
            currentPublication = null;
        }
    }

    private async Task RouteListeningOutputs()
    {
        await foreach (var output in listening.Outputs.ReadAllAsync())
            try
            {
                listeningNotifier.Notify(output);
                if (output is ListeningOutput.ListeningChanged(var value))
                    await bgmMuter.SetListening(value);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "failed to route listening output: {output}", output);
            }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await broadcast.DisposeAsync();
            await broadcastOutputLoop;
            await listening.DisposeAsync();
            await listeningOutputLoop;
        } finally
        {
            try
            {
                await bgmMuter.DisposeAsync();
            } finally
            {
                debugLoopback.SetEnabled(false);
                await debugLoopback.DisposeAsync();
            }
        }
    }
}
