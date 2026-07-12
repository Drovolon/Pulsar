using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Broadcast.Beefweb;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// Hand-cranked IFeed: tests deliver Observations and flip connectivity explicitly,
/// standing in for a live SSE/polling transport when driving the Watcher.
/// </summary>
public sealed class ControllableFeed : IFeed
{
    private readonly Channel<Observation> observations = Channel.CreateUnbounded<Observation>();
    private volatile bool connected;

    public bool Connected => connected;
    public event Action<bool>? OnConnectedChanged;

    public void SetConnected(bool value)
    {
        if (connected == value) return;
        connected = value;
        OnConnectedChanged?.Invoke(value);
    }

    /// <summary>Delivers one observation (and, like a real transport, marks the feed connected).</summary>
    public void Push(Observation o)
    {
        SetConnected(true);
        observations.Writer.TryWrite(o);
    }

    public async IAsyncEnumerable<Observation> Observations([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var o in observations.Reader.ReadAllAsync(ct))
            yield return o;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
