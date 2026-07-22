using System;
using System.Threading.Tasks;
using Pulsar.Concurrency;
using Pulsar.Ipc;

namespace Pulsar;

/// <summary>
/// Debug route from local broadcast outputs back into Pulsar's inbound listening
/// path. This lets you test Pulsar end to end - play something via the broadcast tab,
/// listen to it via the listening tab.
///
/// Enabled by going to Config -> Debug Mode, then in the Debug tab checking
/// the "Debug Loopback" checkbox.
/// </summary>
internal sealed class DebugLoopbackController : IAsyncDisposable
{
    private abstract record Message;
    private sealed record EnabledSet(bool Value, BroadcastPlayerData? Current) : Message;
    private sealed record PlayerDataChanged(BroadcastPlayerData? Data) : Message;

    private readonly IpcProvider ipc;
    private readonly SerializedMailbox<Message> mailbox;
    private bool enabled;

    internal DebugLoopbackController(IpcProvider ipc)
    {
        this.ipc = ipc;
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError);
    }

    internal void SetEnabled(bool value, BroadcastPlayerData? current)
        => mailbox.TryPost(new EnabledSet(value, current));

    internal void OnPlayerDataChanged(BroadcastPlayerData? data)
        => mailbox.TryPost(new PlayerDataChanged(data));

    private ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case EnabledSet(var value, var current):
                if (enabled != value)
                {
                    enabled = value;
                    ipc.ApplyDebugLoopback(value ? current : null);
                }
                break;
            case PlayerDataChanged(var data) when enabled:
                ipc.ApplyDebugLoopback(data);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private static void OnMessageError(Exception e, Message message)
        => Plugin.Log.Error(e, "debug loopback message failed: {message}", message);

    public ValueTask DisposeAsync() => mailbox.DisposeAsync();
}
