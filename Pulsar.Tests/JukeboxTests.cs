using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Jukebox is the folder IMusicSource: its Current null-collapse decides
/// "broadcasting or not" for the whole manifest pipeline.
/// </summary>
public class JukeboxTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-jukebox-test-");
    private readonly FakeRemoteEngine engine = new();
    private Jukebox? jukebox;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (jukebox is not null) await jukebox.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private async Task<Jukebox> Create(params string[] tracks)
    {
        foreach (var t in tracks) TestData.CreateTrack(dir, t);
        jukebox = new Jukebox(engine, dir.FullName);
        await jukebox.Initialize();
        return jukebox;
    }

    [Fact]
    public async Task Stopped_jukebox_is_not_broadcasting()
    {
        var jb = await Create("a.mp3");
        Assert.Null(jb.Current); // null == "not broadcasting", the documented contract
    }

    [Fact]
    public async Task Playing_snapshot_carries_the_track_and_meta()
    {
        var jb = await Create("a.mp3");
        jb.Player.Play();
        await TestWait.Assert(() => jb.Current is { IsPlaying: true }, "snapshot goes live");
        var snap = jb.Current!;
        Assert.Equal(jb.Player.CurrentTrack, snap.FilePath);
        Assert.Equal("a.mp3", snap.Meta?.OriginalFileName);
    }

    [Fact]
    public async Task Paused_jukebox_still_has_a_snapshot()
    {
        var jb = await Create("a.mp3");
        jb.Player.Play();
        await TestWait.Assert(() => jb.Current is { IsPlaying: true }, "playing");
        jb.Player.Pause();
        // Paused is NOT a stop: the snapshot holds (syncs as Paused).
        await TestWait.Assert(() => jb.Current is { IsPlaying: false }, "paused snapshot holds");
    }

    [Fact]
    public async Task Player_events_forward_through_the_jukebox()
    {
        var jb = await Create("a.mp3");
        var events = 0;
        jb.SnapshotChanged += _ => Interlocked.Increment(ref events);
        jb.Player.Play();
        await TestWait.Assert(() => events > 0, "snapshot change forwards");
    }

    [Fact]
    public async Task Dispose_stops_engine_playback()
    {
        var jb = await Create("a.mp3");
        jb.Player.Play();
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Playing, "playing");
        await jb.DisposeAsync();
        jukebox = null;
        Assert.Contains("Stop", engine.Ops);
    }
}
