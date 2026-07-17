using System;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Transport scenarios for PollingFeed, run through a real Beefweb.Client against an
/// in-process beefweb server. What matters: player state comes through as observations,
/// and connectivity tracks whether polls actually succeed. Uses a short poll interval;
/// production defaults to 1s.
/// </summary>
public class PollingFeedTests : IAsyncLifetime
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly FakeBeefwebServer server = new();
    private Beefweb.Client.PlayerClient client = null!;
    private PollingFeed feed = null!;
    private FeedProbe probe = null!;

    /// <summary>Starts polling. Scripting the server BEFORE this avoids racing the first poll.</summary>
    private FeedProbe Start()
    {
        client = server.CreateClient();
        feed = new PollingFeed(client, PollInterval);
        return probe = new FeedProbe(feed);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (probe is not null) await probe.DisposeAsync();
        client?.Dispose();
    }

    [Fact]
    public async Task Player_state_arrives_as_observations()
    {
        server.SetPlaying("/music/a.flac", positionSeconds: 42, durationSeconds: 300,
                          artist: "Band", title: "Song");
        Start();

        await TestWait.Assert(() => probe.Seen.Length >= 1, "first poll result");
        var o = probe.Seen[0];
        Assert.Equal("/music/a.flac", o.RawPath);
        Assert.Equal(PlaybackState.Playing, o.State);
        Assert.Equal(TimeSpan.FromSeconds(42), o.Position);
        Assert.Equal(TimeSpan.FromSeconds(300), o.Duration);
        Assert.Equal("Band", o.Artist);
        Assert.Equal("Song", o.Title);
        Assert.True(feed.Connected, "successful poll marks the feed connected");
    }

    [Fact]
    public async Task A_stopped_player_reads_as_an_empty_observation()
    {
        server.SetStopped();
        Start();

        await TestWait.Assert(() => probe.Seen.Length >= 1, "first poll result");
        var o = probe.Seen[0];
        Assert.Null(o.RawPath);
        Assert.Equal(PlaybackState.Stopped, o.State);
        Assert.Equal(TimeSpan.Zero, o.Position);
    }

    [Fact]
    public async Task A_dead_server_yields_nothing_until_it_comes_back()
    {
        server.Down = true;
        Start();

        // Give it several poll cycles: nothing may come through, and the feed
        // must not report connected - the Watcher's give-up logic keys off this.
        await Task.Delay(PollInterval * 6);
        Assert.Empty(probe.Seen);
        Assert.False(feed.Connected);

        server.SetPlaying("/music/a.flac");
        server.Down = false;

        await TestWait.Assert(() => probe.Seen.Length >= 1, "poll succeeds after recovery");
        Assert.True(feed.Connected);
        Assert.Equal([true], probe.Connectivity); // never flapped while down, one rise on recovery
    }

    [Fact]
    public async Task Losing_the_server_mid_stream_reports_disconnected_then_recovers()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "feed is up");

        server.Down = true;
        await TestWait.Assert(() => !feed.Connected, "failed poll reports disconnected");

        server.Down = false;
        await TestWait.Assert(() => feed.Connected, "recovered poll reports connected");
        Assert.Equal([true, false, true], probe.Connectivity);
    }

    [Fact]
    public async Task Repeated_polling_outages_recover_to_the_latest_state_every_time()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "feed is up");

        string[] paths = ["/music/b.flac", "/music/c.flac", "/music/d.flac"];
        for (var cycle = 0; cycle < paths.Length; cycle++)
        {
            server.Down = true;
            await TestWait.Assert(() => !feed.Connected, $"outage #{cycle + 1} reported");

            server.SetPlaying(paths[cycle]);
            server.Down = false;
            await TestWait.Assert(
                () => feed.Connected && probe.Seen is [.., { RawPath: var path }] && path == paths[cycle],
                $"recovery #{cycle + 1} catches up");
        }

        Assert.Equal(
            [true, false, true, false, true, false, true],
            probe.Connectivity);
    }

    [Fact]
    public async Task Cancellation_ends_the_stream()
    {
        server.SetPlaying("/music/a.flac");
        Start();
        await TestWait.Assert(() => probe.Seen.Length >= 1, "feed is up");

        await probe.CancelAndAwaitEnd("polling loop exits on cancellation");
    }
}
