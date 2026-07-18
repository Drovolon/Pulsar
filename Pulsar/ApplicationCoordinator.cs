using System;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Ipc;
using Pulsar.Listening;

namespace Pulsar;

/// <summary>
/// The single application-level consumer of broadcast outputs. BroadcastManager owns
/// broadcast state; this class explicitly routes its resulting facts to the two consumers.
/// </summary>
internal sealed class ApplicationCoordinator : IAsyncDisposable
{
    private sealed record PublishedState((string, string, PulsarCursor)? PlayerData);

    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly IpcProvider ipc;
    private readonly DebugLoopbackController debugLoopback;
    private readonly Task outputLoop;
    private volatile PublishedState published = new(null);

    internal (string, string, PulsarCursor)? CurrentPlayerData => published.PlayerData;

    internal ApplicationCoordinator(
        BroadcastManager broadcast,
        ListeningManager listening,
        IpcProvider ipc,
        DebugLoopbackController debugLoopback)
    {
        this.broadcast = broadcast;
        this.listening = listening;
        this.ipc = ipc;
        this.debugLoopback = debugLoopback;
        outputLoop = RouteOutputs();
    }

    private async Task RouteOutputs()
    {
        await foreach (var output in broadcast.Outputs.ReadAllAsync())
        {
            try
            {
                switch (output)
                {
                    case BroadcastOutput.PlayerDataChanged(var data):
                        published = new PublishedState(data);
                        ipc.PublishPlayerData(data);
                        debugLoopback.OnPlayerDataChanged(data);
                        break;
                    case BroadcastOutput.BroadcastingChanged(var value):
                        listening.SetBroadcasting(value);
                        break;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "failed to route broadcast output: {output}", output);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await broadcast.DisposeAsync();
        await outputLoop;
        debugLoopback.SetEnabled(false, null);
        await debugLoopback.DisposeAsync();
    }
}
