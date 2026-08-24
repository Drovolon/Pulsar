using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The whole beefweb stack wired the way Watcher.Create does it - real SseFeed, real
/// Beefweb.Client, one shared client for the feed and commands - against the in-process
/// server. One scenario, DJ's-eye view: play, pause, change track, stop.
/// </summary>
public class BeefwebEndToEndTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-e2e-test-");
    private readonly FakeBeefwebServer server = new();
    private Watcher? watcher;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (watcher is not null) await watcher.DisposeAsync();
        dir.Delete(true);
    }

    [Fact]
    public async Task A_dj_session_flows_from_sse_frames_to_snapshots()
    {
        var trackA = TestData.CreateTrack(dir, "a.flac");
        var trackB = TestData.CreateTrack(dir, "b.flac");

        // The DJ is already playing when Pulsar connects.
        server.SetPlaying(trackA, 30, 120, "Band", "First");
        var client = server.CreateClient();
        watcher = new Watcher(new SseFeed(client), client, false);

        await TestWait.Assert(() => watcher.Current is not null, "initial state becomes a snapshot");
        var s = watcher.Current!;
        Assert.Equal(trackA, s.FilePath);
        Assert.True(s.IsPlaying);
        Assert.Equal("Band - First", s.Meta.DisplayName);
        Assert.True(watcher.Status.Connected);

        // DJ pauses.
        server.SetPaused();
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current is { IsPlaying: false }, "pause propagates");

        // DJ jumps to the next track.
        server.SetPlaying(trackB, 0, title: "Second");
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current?.FilePath == trackB, "track change propagates");

        // DJ shuts the player down.
        server.SetStopped();
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current is null, "stop clears the snapshot");
        Assert.True(watcher.Status.Connected, "stopped but still connected");
    }

    [Fact]
    public async Task A_trigger_happy_dj_session_preserves_every_meaningful_transition()
    {
        var trackA = TestData.CreateTrack(dir, "a.flac");
        var trackB = TestData.CreateTrack(dir, "b.flac");
        server.SetStopped();
        var client = server.CreateClient();
        watcher = new Watcher(new SseFeed(client), client, false);
        await TestWait.Assert(() => watcher.Status.Connected, "the initial stopped frame arrives");

        var seen = new List<(string? Path, bool? Playing)>();
        watcher.OnSnapshotChanged += snapshot =>
        {
            lock (seen)
            {
                seen.Add((snapshot?.FilePath, snapshot?.IsPlaying));
            }
        };

        // No waits between pushes: all of these frames coexist in the SSE/Watcher pipeline.
        server.SetPlaying(trackA);
        server.PushUpdate();
        server.SetPaused();
        server.PushUpdate();
        server.SetPlaying(trackA);
        server.PushUpdate();
        server.SetPaused();
        server.PushUpdate();
        server.SetPlaying(trackB, 0);
        server.PushUpdate();
        server.SetPlaying(trackA, 0);
        server.PushUpdate();
        server.SetStopped();
        server.PushUpdate();
        server.SetPlaying(trackA, 0);
        server.PushUpdate();

        await TestWait.Assert(() =>
        {
            lock (seen)
            {
                return seen.Count >= 8;
            }
        }, "all eight cursor transitions flow through");

        (string? Path, bool? Playing)[] actual;
        lock (seen)
        {
            actual = [.. seen];
        }

        Assert.Equal([
            (trackA, true),
            (trackA, false),
            (trackA, true),
            (trackA, false),
            (trackB, true),
            (trackA, true),
            (null, null),
            (trackA, true),
        ], actual);
        Assert.Equal(trackA, watcher.Current?.FilePath);
        Assert.True(watcher.Current?.IsPlaying);
    }
}
