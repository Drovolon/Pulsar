using System;
using System.Threading.Tasks;
using Pulsar.Concurrency;
using Pulsar.Ipc;

namespace Pulsar;

/// <summary>
/// Optional debug-only route from local broadcast output back into Pulsar's inbound
/// player-data path. A mailbox linearizes UI enablement with broadcast updates.
/// </summary>
internal sealed class DebugLoopbackController : IAsyncDisposable
{
    private abstract record Message;
    private sealed record EnabledSet(
        bool Value,
        BroadcastPlayerData? Current,
        TaskCompletionSource Completion) : Message;
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
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (mailbox.TryPost(new EnabledSet(value, current, completion)))
            completion.Task.GetAwaiter().GetResult();
    }

    internal void OnPlayerDataChanged(BroadcastPlayerData? data)
        => mailbox.TryPost(new PlayerDataChanged(data));

    private ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case EnabledSet(var value, var current, var completion):
                if (enabled != value)
                {
                    enabled = value;
                    ipc.ApplyDebugLoopback(value ? current : null);
                }
                completion.TrySetResult();
                break;
            case PlayerDataChanged(var data) when enabled:
                ipc.ApplyDebugLoopback(data);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private static void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Error(e, "debug loopback message failed: {message}", message);
        if (message is EnabledSet(_, _, var completion)) completion.TrySetException(e);
    }

    public ValueTask DisposeAsync() => mailbox.DisposeAsync();
}
