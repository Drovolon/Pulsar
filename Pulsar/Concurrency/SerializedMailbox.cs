using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Pulsar.Concurrency;

/// <summary>
/// Process-local "actor" mailbox. Messages are serial, FIFO order. Completing the mailbox
/// rejects new messages, drains accepted messages, then runs the optional teardown callback.
/// </summary>
internal sealed class SerializedMailbox<T> : IAsyncDisposable
{
    private readonly Channel<T> channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<T, ValueTask> handler;
    private readonly Action<Exception, T>? onError;
    private readonly Func<ValueTask>? onCompleted;
    private readonly Task loop;

    private readonly Lock disposeLock = new();
    private bool accepting = true;
    private Task? disposeTask;

    internal SerializedMailbox(
        Func<T, ValueTask> handler, Action<Exception, T>? onError = null, Func<ValueTask>? onCompleted = null)
    {
        this.handler = handler;
        this.onError = onError;
        this.onCompleted = onCompleted;
        loop = RunAsync();
    }

    internal bool TryPost(T message) => Volatile.Read(ref accepting) && channel.Writer.TryWrite(message);

    private async Task RunAsync()
    {
        await foreach (var message in channel.Reader.ReadAllAsync())
            try
            {
                await handler(message);
            }
            catch (Exception e)
            {
                if (onError is null) throw;
                onError(e, message);
            }

        if (onCompleted is not null) await onCompleted();
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeLock)
        {
            if (disposeTask is null)
            {
                Volatile.Write(ref accepting, false);
                channel.Writer.TryComplete();
                disposeTask = loop;
            }

            return new ValueTask(disposeTask);
        }
    }
}
