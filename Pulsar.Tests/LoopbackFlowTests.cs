using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Broadcast.Prepare;
using Pulsar.Ipc;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// End-to-end flows over the real broadcast->listen chain. ApplicationCoordinator routes
/// typed broadcast outputs to IPC and ListeningManager; the loopback pair then drives the listener
/// engine. Only the process boundaries (audio host RPC, transcode host RPC) are faked.
/// </summary>
public class LoopbackFlowTests : IAsyncLifetime
{
    private const ulong MonitorIdent = ulong.MaxValue;

    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-e2e-test-");
    private readonly ControllablePrepareService service = new();
    private readonly FakeRemoteEngine listenEngine = new();
    private readonly Configuration config = TestData.QuietConfiguration();
    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly FakeIpcGates gates = new();
    private readonly IpcProvider ipc;
    private readonly DebugLoopbackController debugLoopback;
    private readonly ApplicationCoordinator coordinator;
    private readonly FakeGameBgmControl gameBgm = new();

    public LoopbackFlowTests()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(new FakeRemoteEngine(), null!, prep, config);
        listening = new ListeningManager(listenEngine, config);

        // The loopback rides IpcProvider's real JSON and inbound mapping path:
        // broadcast → coordinator → serialize/deserialize+map → listening.
        ipc = new IpcProvider(gates.Gates, listening, broadcast);
        ipc.Prepare();
        debugLoopback = new DebugLoopbackController(ipc);
        coordinator = new ApplicationCoordinator(
            broadcast,
            listening,
            ipc,
            debugLoopback,
            new ListeningNotifier(config),
            new BgmMuter(config, gameBgm));
        debugLoopback.SetEnabled(true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        ipc.Dispose();
        await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static SourceSnapshot Snap(string file, bool playing = true)
        => TestData.Snap(file, playing);

    private string? LatestSyncedFile
        => broadcast.CurrentPlayerData()?.CurrentPath;

    // Synced paths are content-hash names: identify tracks by the manifest's OriginalFileName.
    private string? LatestManifestName
        => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName;

    /// <summary>Start the DJ on a track and pin the loopback pair, like a real loopback session.</summary>
    private async Task<FakeMusicSource> StartLoopbackSession(string track)
    {
        var source = new FakeMusicSource { Current = Snap(track) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        // Wait for the PAIR, not the manifest: the manifest lands a hair earlier, and
        // pinning a missing pair is a silent no-op.
        await TestWait.Assert(() => listening.View.Any(p => p.Ident == MonitorIdent), "loopback pair registered");
        listening.SetActive(MonitorIdent); // pin: playback is otherwise suppressed while we broadcast
        return source;
    }

    [Fact]
    public async Task A_paused_broadcast_still_routes_the_broadcast_bgm_mute_reason()
    {
        var source = new FakeMusicSource { Current = Snap(CreateTrack("paused.flac"), playing: false) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        await TestWait.Assert(() => gameBgm.Muted, "broadcasting mutes game BGM");

        broadcast.SetOnAir(false);
        await TestWait.Assert(() => !gameBgm.Muted, "ending the broadcast restores game BGM");
    }

    [Fact]
    public async Task Debug_loopback_enablement_drives_future_tracks_without_ui_polling()
    {
        debugLoopback.SetEnabled(false);
        var first = CreateTrack("first.flac");
        var source = new FakeMusicSource { Current = Snap(first) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        await TestWait.Assert(() => LatestManifestName == "first.flac", "first manifest is published");
        Assert.DoesNotContain(listening.View, p => p.Ident == MonitorIdent);

        debugLoopback.SetEnabled(true);
        await TestWait.Assert(
            () => listening.View.Any(p => p is { Ident: MonitorIdent, Meta.OriginalFileName: "first.flac" }),
            "enabling immediately routes the current manifest");

        var second = CreateTrack("second.flac");
        source.Current = Snap(second);
        source.RaiseChanged();

        await TestWait.Assert(
            () => listening.View.Any(p => p is { Ident: MonitorIdent, Meta.OriginalFileName: "second.flac" }),
            "later track changes route without any UI draw");
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

        var expected = config.ListeningMasterVolume * TestData.DbToLinear(service.GainDb);
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
    public async Task Skipping_to_a_slow_track_then_back_never_leaks_the_skipped_track_to_the_listener()
    {
        var first = CreateTrack("first.flac");
        var source = await StartLoopbackSession(first);
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "first track playing");
        var firstSynced = LatestSyncedFile;

        var skipped = CreateTrack("skipped.flac");
        var skippedGate = service.GateFor(skipped);
        source.Current = Snap(skipped);
        source.RaiseChanged();
        Assert.True(await service.WaitForPrepare(skipped), "skipped track starts preparing");
        Assert.Equal(firstSynced, listenEngine.Snapshot.Path);

        source.Current = Snap(first);
        source.RaiseChanged();
        await TestWait.Assert(
            () => LatestManifestName == "first.flac"
                  && listenEngine.Snapshot.Path == firstSynced
                  && listenEngine.Snapshot.State == PlaybackState.Playing,
            "returning to the prepared first track converges");

        skippedGate.Open();
        await Task.Delay(200); // a late completion must not disturb the converged state
        Assert.Equal("first.flac", LatestManifestName);
        Assert.Equal(firstSynced, listenEngine.Snapshot.Path);
    }

    [Fact]
    public async Task Switching_sources_keeps_loopback_playing_until_the_new_source_is_ready()
    {
        // The real-world case: switching from the mod player to beefweb.
        await StartLoopbackSession(CreateTrack("mod-song.flac"));
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "playing");

        var beefwebTrack = CreateTrack("foobar-song.flac");
        var gate = service.GateFor(beefwebTrack);
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(beefwebTrack) });

        await Task.Delay(200);
        Assert.Equal("mod-song.flac", LatestManifestName);
        Assert.Equal(PlaybackState.Playing, listenEngine.Snapshot.State);

        // The new source replaces the old one without a stop in between.
        gate.Open();
        await TestWait.Assert(
            () => listenEngine.Snapshot.Path is { } p && p == LatestSyncedFile
                  && LatestManifestName == "foobar-song.flac"
                  && listenEngine.Snapshot.State == PlaybackState.Playing,
            "loopback resumes on the new source's track");
    }

    [Fact]
    public async Task Local_to_beefweb_and_back_reactivates_the_unchanged_local_cursor()
    {
        var localTrack = CreateTrack("mod-song.flac");
        await StartLoopbackSession(localTrack);
        await TestWait.Assert(() => listenEngine.Snapshot.State == PlaybackState.Playing, "local plays");
        var localArtifact = LatestSyncedFile;

        var beefwebTrack = CreateTrack("beefweb-song.flac");
        await broadcast.InstallProviderForTests(
            BroadcastProvider.Beefweb,
            new FakeMusicSource { Current = Snap(beefwebTrack) });
        broadcast.SetProvider(BroadcastProvider.Beefweb);
        await TestWait.Assert(
            () => LatestManifestName == "beefweb-song.flac"
                  && listenEngine.Snapshot.Path == LatestSyncedFile,
            "beefweb plays");

        broadcast.SetProvider(BroadcastProvider.Local);
        await TestWait.Assert(
            () => LatestManifestName == "mod-song.flac"
                  && listenEngine.Snapshot.Path == localArtifact
                  && listenEngine.Snapshot.State == PlaybackState.Playing
                  && listening.Playback.Status == ListenerPlaybackStatus.Playing,
            "the existing local provider becomes live again");

        Assert.Equal(ListenerPlaybackStatus.Playing, listening.Playback.Status);
    }

    [Fact]
    public async Task Loopback_pair_returns_after_a_real_local_to_beefweb_gap()
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "local"));
        TestData.CreateTrack(folder, "local.flac");
        await broadcast.LoadFolder(folder.FullName);
        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(
            () => listening.View.Any(p => p is { Ident: MonitorIdent, Meta.OriginalFileName: "local.flac" }),
            "the local broadcast reaches loopback");

        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        await broadcast.SetBeefwebSource(new Watcher(feed, server.CreateClient(), false));
        await TestWait.Assert(
            () => listening.View.All(p => p.Ident != MonitorIdent),
            "stopping the local monitor clears loopback during the switch");

        var beefwebTrack = CreateTrack("beefweb.flac");
        var gate = service.GateFor(beefwebTrack);
        feed.Push(new Observation(
            beefwebTrack,
            PlaybackState.Playing,
            TimeSpan.Zero,
            TimeSpan.FromMinutes(3),
            "Beefweb",
            "DJ",
            DateTimeOffset.UtcNow,
            System.Diagnostics.Stopwatch.GetTimestamp()));
        Assert.True(await service.WaitForPrepare(beefwebTrack), "the Beefweb track starts preparing");
        gate.Open();

        await TestWait.Assert(
            () => listening.View.Any(p => p is { Ident: MonitorIdent, Meta.OriginalFileName: "beefweb.flac" }),
            "the Beefweb broadcast recreates the loopback pair");
    }

    [Fact]
    public async Task A_nearby_dj_does_not_leak_through_a_source_switch_prep_gap()
    {
        // We're the DJ (nothing pinned); Bob broadcasts nearby. Browsing another
        // folder and then playing from it must not create a real broadcast stop.
        var firstFolder = Directory.CreateDirectory(Path.Combine(dir.FullName, "set-one"));
        TestData.CreateTrack(firstFolder, "set-one.flac");
        await broadcast.LoadFolder(firstFolder.FullName);
        var source = broadcast.ActiveLocalSource!;
        broadcast.SetOnAir(true);
        source.Play();
        await TestWait.Assert(() => LatestSyncedFile is not null, "our broadcast starts");

        listening.AddOrUpdatePair(42, "Bob", new PairData(
            @"C:\sync\bob.opus", TimeSpan.Zero, true, DateTimeOffset.UtcNow, 1, null));
        await Task.Delay(200);
        Assert.Null(listenEngine.Snapshot.Path); // no tuning in: we're the DJ

        var secondFolder = Directory.CreateDirectory(Path.Combine(dir.FullName, "set-two"));
        var slow = TestData.CreateTrack(secondFolder, "set-two.flac");
        var gate = service.GateFor(slow);
        await broadcast.LoadFolder(secondFolder.FullName); // browse-only: set one stays live
        Assert.Equal("set-one.flac", LatestManifestName);
        source.PlayNow(source.Library[0]);
        Assert.True(await service.WaitForPrepare(slow), "prep started");

        await Task.Delay(300);
        Assert.Null(listenEngine.Snapshot.Path); // still the DJ: Bob must not leak in
        gate.Open();
        await TestWait.Assert(() => LatestManifestName == "set-two.flac", "our new manifest goes out");
        Assert.Null(listenEngine.Snapshot.Path);

        // Only a REAL stop hands the airwaves back to autoplay.
        broadcast.SetOnAir(false);
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
        // a terminal Failed update; the respawned host starts fresh (volume 1, nothing
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
        var expected = config.ListeningMasterVolume * 0.5f * TestData.DbToLinear(service.GainDb);
        await TestWait.Assert(() => Math.Abs(listenEngine.LastVolume - expected) < 0.001f,
            "volume is pushed to the fresh host");
    }
}
