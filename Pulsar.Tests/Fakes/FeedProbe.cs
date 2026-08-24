using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Broadcast.Beefweb;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// Consumes a feed the way the Watcher does - one background enumeration - and
/// records every observation and connectivity transition for assertions.
/// </summary>
public sealed class FeedProbe : IAsyncDisposable
{
    private readonly CancellationTokenSource cts = new();
    private readonly List<Observation> seen = [];
    private readonly List<bool> connectivity = [];
    private readonly IFeed feed;
    private readonly Task pump;

    public FeedProbe(IFeed feed)
    {
        this.feed = feed;
        feed.OnConnectedChanged += OnConnectedChanged;
        pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var o in feed.Observations(cts.Token))
                    lock (seen)
                    {
                        seen.Add(o);
                    }
            }
            catch (OperationCanceledException) { }
        });
    }

    private void OnConnectedChanged(bool connected)
    {
        lock (seen)
        {
            connectivity.Add(connected);
        }
    }

    public Observation[] Seen
    {
        get
        {
            lock (seen)
            {
                return [.. seen];
            }
        }
    }

    public bool[] Connectivity
    {
        get
        {
            lock (seen)
            {
                return [.. connectivity];
            }
        }
    }

    /// <summary>Cancels the enumeration and asserts it actually ends.</summary>
    public async Task CancelAndAwaitEnd(string because)
    {
        cts.Cancel();
        await TestWait.Within(pump, because);
    }

    public async ValueTask DisposeAsync()
    {
        feed.OnConnectedChanged -= OnConnectedChanged;
        cts.Cancel();
        try
        {
            await TestWait.Within(pump, "feed probe pump exits during disposal");
        } finally
        {
            cts.Dispose();
            await feed.DisposeAsync();
        }
    }
}
