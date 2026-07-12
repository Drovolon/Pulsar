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
        dir.Delete(recursive: true);
    }

    [Fact]
    public async Task A_dj_session_flows_from_sse_frames_to_snapshots()
    {
        var trackA = TestData.CreateTrack(dir, "a.flac");
        var trackB = TestData.CreateTrack(dir, "b.flac");

        // The DJ is already playing when Pulsar connects.
        server.SetPlaying(trackA, positionSeconds: 30, durationSeconds: 120,
                          artist: "Band", title: "First");
        var client = server.CreateClient();
        watcher = new Watcher(new SseFeed(client), client, isWine: false);

        await TestWait.Assert(() => watcher.Current is not null, "initial state becomes a snapshot");
        var s = watcher.Current!;
        Assert.Equal(trackA, s.FilePath);
        Assert.True(s.IsPlaying);
        Assert.Equal("First", s.Meta.Title);
        Assert.True(watcher.Status.Connected);

        // DJ pauses.
        server.SetPaused();
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current is { IsPlaying: false }, "pause propagates");

        // DJ jumps to the next track.
        server.SetPlaying(trackB, positionSeconds: 0, title: "Second");
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current?.FilePath == trackB, "track change propagates");

        // DJ shuts the player down.
        server.SetStopped();
        server.PushUpdate();
        await TestWait.Assert(() => watcher.Current is null, "stop clears the snapshot");
        Assert.True(watcher.Status.Connected, "stopped but still connected");
    }
}
