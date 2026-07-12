using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Prepare;
using Pulsar.Ipc;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// End-to-end flows over the real broadcast->listen chain, glued together the same
/// way Plugin + DebugTab wire the loopback: OnPlayerDataChanged drives SetBroadcasting
/// and the loopback pair (SetPlayerData/ClearPlayerData), which drives the listener
/// engine. Only the process boundaries (audio host RPC, transcode host RPC) are faked.
/// </summary>
public class LoopbackFlowTests : IAsyncLifetime
{
    private const ulong MonitorIdent = ulong.MaxValue;

    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-e2e-test-");
    private readonly ControllablePrepareService service = new();
    private readonly FakeRemoteEngine listenEngine = new();
    private readonly List<(string, string[], PulsarCursor)?> manifests = [];

    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly FakeIpcGates gates = new();
    private readonly IpcProvider ipc;

    public LoopbackFlowTests()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(new FakeRemoteEngine(), null!, prep, () => new Configuration());
        listening = new ListeningManager(listenEngine);

        // Same wiring as Plugin.cs: "broadcasting" means we have a live source, carried
        // on its own edge-detected event rather than the manifest wire.
        broadcast.OnBroadcastingChanged += b => listening.SetBroadcasting(b);
        // Keep the raw manifest feed purely for assertions.
        broadcast.OnPlayerDataChanged += data =>
        {
            lock (manifests) manifests.Add(data);
        };

        // The loopback rides the REAL IPC surface end to end: broadcast → IpcProvider
        // serialize → wire triple → deserialize+map → listening. Same loop DebugTab's
        // monitor drives in production.
        ipc = new IpcProvider(gates.Gates, listening, broadcast);
        ipc.Prepare();
        gates.PlayerDataChanged.OnSent = args =>
        {
            var (file, prefetch, cursorJson) = ((string)args[0]!, (string[])args[1]!, (string)args[2]!);
            if (file.Length == 0) gates.ClearPlayerData.Action!(MonitorIdent);
            else gates.SetPlayerData.Action!(MonitorIdent, file, prefetch, cursorJson);
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        ipc.Dispose();
        await broadcast.DisposeAsync();
        await listening.DisposeAsync();
        await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static SourceSnapshot Snap(string file, bool playing = true)
        => TestData.Snap(file, playing);

    private string? LatestSyncedFile
    {
        get { lock (manifests) return manifests.LastOrDefault(m => m is not null)?.Item1; }
    }

    // Synced paths are content-hash names: identify tracks by the manifest's OriginalFileName.
    private string? LatestManifestName
    {
        get { lock (manifests) return manifests.LastOrDefault(m => m is not null)?.Item3.Meta?.OriginalFileName; }
    }

    /// <summary>Start the DJ on a track and pin the loopback pair, like a real loopback session.</summary>
    private async Task<FakeMusicSource> StartLoopbackSession(string track)
    {
        var source = new FakeMusicSource { Current = Snap(track) };
        await broadcast.SetSource(source);
        // Wait for the PAIR, not the manifest: the manifest lands a hair earlier, and
        // pinning a missing pair is a silent no-op.
        await TestWait.Assert(() => listening.View.Any(p => p.Ident == MonitorIdent), "loopback pair registered");
        listening.SetActive(MonitorIdent); // pin: playback is otherwise suppressed while we broadcast
        return source;
    }

    [Fact]
    public async Task Dj_playback_reaches_the_listener_with_replaygain_applied()
    {
        await StartLoopbackSession(CreateTrack("song.flac"));

        // The listener plays the PREPARED artifact named by the manifest - never the
        // DJ's original file - with the cursor-carried gain folded into its volume.
        await TestWait.Assert(
            () => listenEngine.Snapshot is { State: PlaybackState.Playing } s && s.Path == LatestSyncedFile,
            "listener plays the synced artifact");
        Assert.NotEqual("song.flac", Path.GetFileName(LatestSyncedFile!));

        var expected = TestData.DbToLinear(service.GainDb);
        await TestWait.Assert(() => Math.Abs(listenEngine.LastVolume - expected) < 0.001f,
            "the manifest's ReplayGain shapes the listener volume");
    }

    [Fact]
    public async Task Dj_pause_and_resume_reach_the_listener()
    {
        var track = CreateTrack("song.flac");
        var source = await StartLoopbackSession(track);
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");

        source.Current = Snap(track, playing: false);
        source.RaiseChanged();
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Paused,
            "DJ pause pauses the listener");

        source.Current = Snap(track);
        source.RaiseChanged();
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing,
            "DJ resume resumes the listener");
    }

    [Fact]
    public async Task Slow_prep_on_track_change_keeps_the_listener_on_the_old_song_until_ready()
    {
        var source = await StartLoopbackSession(CreateTrack("current.flac"));
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");
        var oldSynced = LatestSyncedFile;

        var next = CreateTrack("next.flac");
        var gate = service.GateFor(next);
        source.Current = Snap(next);
        source.RaiseChanged();

        // Transcode in flight: the listener must NOT be interrupted - no stop, no swap.
        Assert.True(await service.WaitForPrepare(next), "prep started");
        await Task.Delay(300);
        Assert.Equal(oldSynced, listenEngine.Snapshot.Path);
        Assert.Equal(PlaybackState.Playing, listenEngine.Snapshot.State);

        gate.Open();

        await TestWait.Assert(
            () => listenEngine.Snapshot.Path is { } p && p != oldSynced && p == LatestSyncedFile,
            "listener swaps to the new track once it's prepared");
    }

    [Fact]
    public async Task Switching_sources_stops_the_loopback_then_the_new_source_plays()
    {
        // The real-world case: switching from the mod player to beefweb.
        await StartLoopbackSession(CreateTrack("mod-song.flac"));
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");

        var beefwebTrack = CreateTrack("foobar-song.flac");
        var gate = service.GateFor(beefwebTrack);
        await broadcast.SetSource(new FakeMusicSource { Current = Snap(beefwebTrack) });

        await TestWait.Assert(() => listenEngine.Snapshot.Path is null,
            "loopback playback stops on source switch");

        // The new source's track finishes preparing: the loopback resumes on it
        // (the pin is by name and survives the pair being cleared).
        gate.Open();
        await TestWait.Assert(
            () => listenEngine.Snapshot.Path is { } p && p == LatestSyncedFile
                  && LatestManifestName == "foobar-song.flac"
                  && listenEngine.Snapshot.State == PlaybackState.Playing,
            "loopback resumes on the new source's track");
    }

    [Fact]
    public async Task A_nearby_dj_does_not_leak_through_a_source_switch_prep_gap()
    {
        // We're the DJ (nothing pinned); Bob broadcasts nearby. While our source switch
        // waits on a slow transcode, autoplay must not tune into Bob for the gap.
        var source = new FakeMusicSource { Current = Snap(CreateTrack("set-one.flac")) };
        await broadcast.SetSource(source);
        await TestWait.Assert(() => LatestSyncedFile is not null, "our broadcast starts");

        listening.AddOrUpdatePair(42, "Bob", new PairData(
            @"C:\sync\bob.opus", TimeSpan.Zero, true, DateTimeOffset.UtcNow, 1, null));
        await Task.Delay(200);
        Assert.Null(listenEngine.Snapshot.Path); // no tuning in: we're the DJ

        var slow = CreateTrack("set-two.flac");
        var gate = service.GateFor(slow);
        await broadcast.SetSource(new FakeMusicSource { Current = Snap(slow) });
        Assert.True(await service.WaitForPrepare(slow), "prep started");

        await Task.Delay(300);
        Assert.Null(listenEngine.Snapshot.Path); // still the DJ: Bob must not leak in
        gate.Open();
        await TestWait.Assert(() => LatestManifestName == "set-two.flac", "our new manifest goes out");
        Assert.Null(listenEngine.Snapshot.Path);

        // Only a REAL stop hands the airwaves back to autoplay.
        await broadcast.SetSource(null);
        await TestWait.Assert(() => listenEngine.Snapshot.Path == @"C:\sync\bob.opus",
            "Bob plays once we actually stop DJing");
    }

    [Fact]
    public async Task Dj_stop_silences_the_listener()
    {
        var source = await StartLoopbackSession(CreateTrack("song.flac"));
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");

        source.Current = null; // hard stop, not a pause
        source.RaiseChanged();

        await TestWait.Assert(() => listenEngine.Snapshot.Path is null, "listener stops");
    }

    [Fact]
    public async Task Listen_host_crash_recovery_reloads_the_track_and_the_volume()
    {
        // ReconnectingEngine's documented crash sequence: a dying host synthesizes
        // OnPlaybackEnded(Failed); the respawned host starts fresh (volume 1, nothing
        // loaded) and OnReconnected fires. The listener must re-load and re-volume.
        var track = CreateTrack("song.flac");
        await StartLoopbackSession(track);
        listening.SetPairVolume(MonitorIdent, 0.5f);
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");
        var loadsBefore = listenEngine.Ops.Count(o => o == "Load");

        listenEngine.FailTrack();          // synth Failed: host died mid-track
        listening.OnEngineReconnected();   // host respawned

        await TestWait.Assert(
            () => listenEngine.Ops.Count(o => o == "Load") > loadsBefore
                  && listenEngine.Snapshot.State == PlaybackState.Playing,
            "track reloads after the host restart");
        var expected = 0.5f * TestData.DbToLinear(service.GainDb);
        await TestWait.Assert(() => Math.Abs(listenEngine.LastVolume - expected) < 0.001f,
            "volume is pushed to the fresh host");
    }
}
