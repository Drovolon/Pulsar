using System;
using System.Net.Http;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Transport scenarios for SseFeed, run through a real Beefweb.Client SSE stream against
/// an in-process beefweb server. The invariant: pushed updates flow promptly (no polling
/// lag), and a dropped stream reconnects by itself. Uses a short backoff; production
/// defaults to 2s.
/// </summary>
public class SseFeedTests : IAsyncLifetime
{
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(50);

    private readonly FakeBeefwebServer server = new();
    private Beefweb.Client.PlayerClient client = null!;
    private SseFeed feed = null!;
    private FeedProbe probe = null!;

    /// <summary>Subscribes. Scripting the server BEFORE this avoids racing the initial frame.</summary>
    private FeedProbe Start()
    {
        client = server.CreateClient();
        feed = new SseFeed(client, Backoff);
        return probe = new FeedProbe(feed);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (probe is not null) await probe.DisposeAsync();
        client?.Dispose();
    }

    [Fact]
    public async Task Subscribing_delivers_the_current_state_immediately()
    {
        server.SetPlaying("/music/a.flac", positionSeconds: 42, durationSeconds: 300,
                          artist: "Band", title: "Song");
        Start();

        // beefweb sends the state as of subscription - no DJ action needed.
        await TestWait.Assert(() => probe.Seen.Length >= 1, "initial SSE frame");
        var o = probe.Seen[0];
        Assert.Equal("/music/a.flac", o.RawPath);
        Assert.Equal(PlaybackState.Playing, o.State);
        Assert.Equal("Band", o.Artist);
        Assert.Equal("Song", o.Title);
        Assert.True(feed.Connected);
    }

    [Fact]
    public async Task Pushed_updates_arrive_in_order()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "initial SSE frame");

        server.SetPaused();
        server.PushUpdate();
        server.SetPlaying("/music/b.flac", positionSeconds: 0);
        server.PushUpdate();

        await TestWait.Assert(() => probe.Seen.Length >= 3, "both pushed frames");
        Assert.Equal(PlaybackState.Paused, probe.Seen[1].State);
        Assert.Equal("/music/b.flac", probe.Seen[2].RawPath);
        Assert.Equal(PlaybackState.Playing, probe.Seen[2].State);
    }

    // A beefweb restart can end the stream cleanly or sever it mid-connection;
    // either way the feed must notice and resubscribe on its own.
    [Theory]
    [InlineData(false)] // server closed the stream
    [InlineData(true)]  // transport error
    public async Task A_dropped_stream_reconnects_on_its_own(bool abruptly)
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "initial SSE frame");

        server.DropSseStreams(abruptly ? new HttpRequestException("connection reset") : null);
        await TestWait.Assert(() => !feed.Connected, "drop reported");

        // The DJ moved on while we were away; the resubscribe's initial frame catches us up.
        server.SetPlaying("/music/b.flac");
        await TestWait.Assert(
            () => probe.Seen is [.., { RawPath: "/music/b.flac" }],
            "state after reconnect");
        Assert.True(feed.Connected);
        Assert.Equal(2, server.SseSessionsOpened);
        Assert.Equal([true, false, true], probe.Connectivity);
    }

    [Fact]
    public async Task Repeated_stream_drops_reconnect_and_catch_up_every_time()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "initial SSE frame");

        string[] paths = ["/music/b.flac", "/music/c.flac", "/music/d.flac"];
        for (var cycle = 0; cycle < paths.Length; cycle++)
        {
            server.DropSseStreams(cycle % 2 == 0 ? null : new HttpRequestException("connection reset"));
            await TestWait.Assert(() => !feed.Connected, $"drop #{cycle + 1} reported");

            server.SetPlaying(paths[cycle]);
            await TestWait.Assert(
                () => feed.Connected && probe.Seen is [.., { RawPath: var path }] && path == paths[cycle],
                $"reconnect #{cycle + 1} catches up");
            Assert.Equal(cycle + 2, server.SseSessionsOpened);
        }

        Assert.Equal(
            [true, false, true, false, true, false, true],
            probe.Connectivity);
    }

    [Fact]
    public async Task Cancellation_ends_the_stream_without_reconnecting()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "initial SSE frame");

        await probe.CancelAndAwaitEnd("SSE loop exits on cancellation");

        await Task.Delay(Backoff * 6); // well past the reconnect backoff
        Assert.Equal(1, server.SseSessionsOpened);
    }
}
