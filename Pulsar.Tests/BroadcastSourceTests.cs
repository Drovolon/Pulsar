using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Broadcast.Local;
using Pulsar.Broadcast.Prepare;
using Pulsar.Listening;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;
using PulsarState = NAudio.Wave.PlaybackState;

namespace Pulsar.Tests;

/// <summary>
/// BroadcastManager's source lifecycle beyond the manifest pipeline: the prefetch
/// timer (lead + final checkpoints, disarm rules), overlapping source switches, and
/// the folder/mod loaders. The prefetch-timer tests mutate the source's Current
/// WITHOUT raising an event - only the timer reads live state, so a prefetch for a
/// track no event ever carried must have come from it.
/// </summary>
[Collection(ChatNotificationCollection.Name)]
public class BroadcastSourceTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-bsrc-test-");
    private readonly ControllablePrepareService service = new();
    private readonly FakeRemoteEngine engine = new();
    private readonly FakeModResolver resolver = new();
    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;

    private readonly Configuration config = new();
    private readonly PrefetchTiming prefetchTiming = new();

    public BroadcastSourceTests()
    {
        TestBootstrap.Chat.Clear();
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(engine, resolver, prep, config, prefetchTiming);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await broadcast.DisposeAsync();
        await prep.DisposeAsync();
        dir.Delete(true);
    }

    private sealed class FakeModResolver : IModResolver
    {
        public string? Result;
        public string? ResolveModDirectory(string modDirectoryName) => Result;
    }

    private sealed class GatedCatalogLoader(string root) : ITrackCatalogLoader
    {
        private int calls;
        public string RootDirectory { get; } = root;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource<TrackCatalog> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return Result.Task;
        }
    }

    private string Track(string name) => TestData.CreateTrack(dir, name);

    /// <summary>A snapshot whose duration/next the prefetch timer reads.</summary>
    private static SourceSnapshot TimedSnap(string file, long durationMs, string? next = null, bool playing = true) =>
        new(file, next, playing, TimeSpan.Zero, DateTimeOffset.UtcNow,
            new TrackMeta { OriginalFileName = Path.GetFileName(file), DurationMs = durationMs });

    // ---- prefetch timer ---------------------------------------------------------

    [Fact]
    public async Task The_lead_checkpoint_prefetches_the_live_next_track()
    {
        // dur 1000ms, lead 700ms -> lead tick ~300ms after arming.
        prefetchTiming.LeadMs = 700;
        prefetchTiming.FinalMs = 100;
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, 1000) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");
        Assert.False(await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(150)),
                     "nothing can have prefetched 'next' yet - no event ever carried it");

        // Queue changes keep the playback ID but wake consumers.
        source.RaiseProjectionChanged(TimedSnap(current, 1000, next));

        Assert.True(await service.WaitForPrepare(next), "the lead tick prefetched the next track");
        await TestWait.Assert(() => broadcast.CurrentPlayerData()?.PrefetchPath is { Length: > 0 },
                              "the completed prefetch reaches the published manifest");
    }

    [Fact]
    public async Task Inside_the_lead_window_the_final_checkpoint_still_fires()
    {
        // lead (5s) > duration (1s): arming lands INSIDE the lead window, so the
        // FINAL checkpoint is scheduled directly (~700ms: remaining 1000 - final 300).
        prefetchTiming.LeadMs = 5000;
        prefetchTiming.FinalMs = 300;
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, 1000) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.RaiseProjectionChanged(TimedSnap(current, 1000, next));

        Assert.True(await service.WaitForPrepare(next), "the final tick prefetched the next track");
    }

    [Fact]
    public async Task A_completed_prefetch_is_discarded_if_the_live_queue_head_changed()
    {
        prefetchTiming.LeadMs = 700;
        prefetchTiming.FinalMs = 100;
        var current = Track("current.flac");
        var staleNext = Track("stale-next.flac");
        var liveNext = Track("live-next.flac");
        var staleGate = service.GateFor(staleNext);
        var source = new FakeMusicSource
        {
            Current = TimedSnap(current, 1000, staleNext),
        };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);

        Assert.True(await service.WaitForPrepare(staleNext), "the checkpoint samples the old queue head");
        source.RaiseProjectionChanged(TimedSnap(current, 1000, liveNext));
        staleGate.Open();

        await Task.Delay(200);
        Assert.True(string.IsNullOrEmpty(broadcast.CurrentPlayerData()?.PrefetchPath));
    }

    [Fact]
    public async Task A_paused_source_disarms_the_prefetch_timer()
    {
        // If the pause guard regresses, these values arm the erroneous lead tick in
        // ~100ms (1000ms remaining - 900ms lead), comfortably inside the negative wait.
        prefetchTiming.LeadMs = 900;
        prefetchTiming.FinalMs = 50;
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, 1000, playing: false) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);

        source.Current = TimedSnap(current, 1000, next, false); // no event
        await Task.Delay(400);
        Assert.DoesNotContain(next, service.PrepareCalls); // paused: no tick may fire
    }

    [Fact]
    public async Task Unknown_duration_disarms_the_prefetch_timer()
    {
        prefetchTiming.LeadMs = 100;
        prefetchTiming.FinalMs = 50;
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, 0) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.Current = TimedSnap(current, 0, next); // no event
        await Task.Delay(400);
        Assert.DoesNotContain(next, service.PrepareCalls); // unknown duration: no tick may fire
    }

    [Fact]
    public async Task Pausing_cancels_an_armed_timer_and_resuming_arms_a_fresh_one()
    {
        prefetchTiming.LeadMs = 700;
        prefetchTiming.FinalMs = 100;
        var current = Track("current.flac");
        var staleNext = Track("stale-next.flac");
        var liveNext = Track("live-next.flac");
        var source = new FakeMusicSource { Current = TimedSnap(current, 1000) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, source);

        // This observation cancels the playing timer. The next path is installed only
        // after the event, so the paused observation itself cannot prefetch it.
        source.Current = TimedSnap(current, 1000, playing: false);
        source.RaiseChanged();
        source.Current = TimedSnap(current, 1000, staleNext, false);
        await Task.Delay(400); // past the original ~300ms lead checkpoint
        Assert.DoesNotContain(staleNext, service.PrepareCalls);

        source.Current = TimedSnap(current, 1000, playing: true);
        source.RaiseChanged();
        source.Current = TimedSnap(current, 1000, liveNext, true);

        Assert.True(await service.WaitForPrepare(liveNext), "resume arms a fresh checkpoint");
        Assert.DoesNotContain(staleNext, service.PrepareCalls);
    }

    [Fact]
    public async Task Switching_sources_cancels_the_old_sources_armed_timer()
    {
        prefetchTiming.LeadMs = 700;
        prefetchTiming.FinalMs = 100;
        var oldCurrent = Track("old-current.flac");
        var oldNext = Track("old-next.flac");
        var newCurrent = Track("new-current.flac");
        var newNext = Track("new-next.flac");

        var oldSource = new FakeMusicSource { Current = TimedSnap(oldCurrent, 1000) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, oldSource);

        var newSource = new FakeMusicSource { Current = TimedSnap(newCurrent, 1000) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, newSource); // ClearAsync fences off the old timer
        oldSource.Current = TimedSnap(oldCurrent, 1000, oldNext);
        newSource.Current = TimedSnap(newCurrent, 1000, newNext);

        Assert.True(await service.WaitForPrepare(newNext), "the new source owns the checkpoint");
        Assert.DoesNotContain(oldNext, service.PrepareCalls);
    }

    // ---- overlapping switches ---------------------------------------------------

    [Fact]
    public async Task Overlapping_provider_replacements_make_the_last_caller_desired()
    {
        var a = new FakeMusicSource
        {
            Current = TestData.Snap(Track("one.flac")),
            DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, a);

        var t3 = Track("three.flac");
        var b = new FakeMusicSource { Current = TestData.Snap(Track("two.flac")) };
        var c = new FakeMusicSource { Current = TestData.Snap(t3) };

        var toB = broadcast.BroadcastFromForTests(BroadcastProvider.Local, b);
        var toC = broadcast.BroadcastFromForTests(BroadcastProvider.Local, c);

        await TestWait.Within(toB, "switch to b");
        await TestWait.Within(toC, "switch to c");
        await TestWait.Assert(() => broadcast.CurrentSnapshot?.FilePath == t3, "c commits");

        Assert.False(a.Disposed, "the live source is retained until its replacement commits");
        Assert.True(b.Disposed, "b installed then torn down by c's switch");
        Assert.False(c.Disposed, "c is live");
        Assert.Equal(t3, broadcast.CurrentSnapshot?.FilePath);
        a.DisposeGate.TrySetResult();
        await TestWait.Assert(() => a.Disposed, "the retired live source drains asynchronously");
    }

    // ---- loaders ------------------------------------------------------------------

    [Fact]
    public async Task Catalog_loading_does_not_block_events_from_the_current_source()
    {
        var first = Track("first.flac");
        var second = Track("second.flac");
        var current = new FakeMusicSource { Current = TestData.Snap(first) };
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, current);
        var loader = new GatedCatalogLoader(Path.Combine(dir.FullName, "pending"));
        var loading = broadcast.LoadLocalSource(loader);
        await TestWait.Assert(() => loader.Calls == 1, "catalog loading starts");

        current.Current = TestData.Snap(second);
        current.RaiseChanged();

        await TestWait.Assert(() => broadcast.CurrentSnapshot?.FilePath == second,
                              "the current source remains responsive during the scan");
        loader.Result.TrySetResult(
            new TrackCatalog([new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [])]));
        await TestWait.Within(loading, "the catalog load finishes");
    }

    [Fact]
    public async Task Beefweb_stays_off_air_until_the_session_gate_is_enabled()
    {
        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        var watcher = new Watcher(feed, server.CreateClient(), false);
        await broadcast.SetBeefwebSource(watcher);
        var track = Track("paused.flac");

        feed.Push(new Observation(track, PulsarState.Paused, TimeSpan.FromSeconds(12), TimeSpan.FromMinutes(3),
                                  "Paused Song", "Band", DateTimeOffset.UtcNow,
                                  System.Diagnostics.Stopwatch.GetTimestamp()));

        await TestWait.Assert(() => watcher.Current is not null, "Beefweb observes its initial state");
        Assert.False(broadcast.OnAir);
        Assert.Null(broadcast.CurrentSnapshot);
        Assert.Null(broadcast.CurrentPlayerData());

        broadcast.SetOnAir(true);

        await TestWait.Assert(() => broadcast.CurrentSnapshot is not null, "the session gate opens");
        Assert.True(broadcast.OnAir);
        Assert.Equal(track, broadcast.CurrentSnapshot!.FilePath);
        Assert.False(broadcast.CurrentSnapshot.IsPlaying);

        broadcast.SetOnAir(false);

        await TestWait.Assert(() => broadcast.CurrentSnapshot is null, "the session gate closes");
        Assert.False(broadcast.OnAir);
        Assert.Null(broadcast.CurrentPlayerData());
    }

    [Fact]
    public async Task Beefweb_warning_policy_stays_in_the_broadcast_manager()
    {
        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        var watcher = new Watcher(feed, server.CreateClient(), false);
        await broadcast.SetBeefwebSource(watcher);

        feed.Push(new Observation("https://radio.example/off-air", PulsarState.Playing, TimeSpan.Zero,
                                  TimeSpan.FromMinutes(3), "Off Air", "DJ", DateTimeOffset.UtcNow,
                                  System.Diagnostics.Stopwatch.GetTimestamp()));
        await TestWait.Assert(() => watcher.Status.Unsyncable is not null, "Beefweb reports the off-air source");
        await Task.Delay(100);
        Assert.Empty(TestBootstrap.Chat.Messages);

        var track = Track("syncable.mp3");
        feed.Push(new Observation(track, PulsarState.Playing, TimeSpan.Zero, TimeSpan.FromMinutes(3), "Syncable", "DJ",
                                  DateTimeOffset.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp()));
        await TestWait.Assert(() => watcher.Current is not null, "a syncable source resets the warning edge");
        broadcast.SetOnAir(true);
        await TestWait.Assert(() => broadcast.OnAir, "broadcasting is enabled");

        feed.Push(new Observation("https://radio.example/on-air", PulsarState.Playing, TimeSpan.Zero,
                                  TimeSpan.FromMinutes(3), "On Air", "DJ", DateTimeOffset.UtcNow,
                                  System.Diagnostics.Stopwatch.GetTimestamp()));
        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1, "the selected on-air source warns the DJ");
        Assert.Contains("internet radio stream", TestBootstrap.Chat.Messages[0]);
    }

    [Fact]
    public async Task Local_monitoring_stays_private_until_global_on_air_is_enabled()
    {
        var folder = CreateFolder("private.mp3");
        await broadcast.LoadFolder(folder.FullName);
        var player = broadcast.ActiveLocalSource!;
        player.Play();

        await TestWait.Assert(() => engine.Snapshot.State == PulsarState.Playing, "local monitor starts");
        Assert.False(broadcast.OnAir);
        Assert.Null(broadcast.CurrentSnapshot);
        Assert.Null(broadcast.CurrentPlayerData());

        broadcast.SetOnAir(true);
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is not null, "global On Air publishes playback");
        Assert.True(broadcast.OnAir);

        player.Stop();
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is null, "a real stop is published");
        Assert.True(broadcast.OnAir);
        Assert.Null(broadcast.BroadcastStatus.LiveProvider);
    }

    [Fact]
    public async Task Switching_from_local_to_beefweb_stops_the_hidden_local_monitor()
    {
        var folder = CreateFolder("local.mp3");
        await broadcast.LoadFolder(folder.FullName);
        var player = broadcast.ActiveLocalSource!;
        broadcast.SetOnAir(true);
        player.Play();
        await TestWait.Assert(() => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "local.mp3",
                              "local provider is live");

        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        var watcher = new Watcher(feed, server.CreateClient(), false);
        await broadcast.SetBeefwebSource(watcher);
        await TestWait.Assert(() => engine.Snapshot.State == PulsarState.Stopped,
                              "selecting Beefweb stops the Local monitor");
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is null,
                              "the stopped Local source is no longer published");

        var beefwebTrack = Track("beefweb.mp3");
        var gate = service.GateFor(beefwebTrack);
        feed.Push(new Observation(beefwebTrack, PulsarState.Playing, TimeSpan.Zero, TimeSpan.FromMinutes(3), "Beefweb", "DJ",
                                  DateTimeOffset.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp()));

        Assert.True(await service.WaitForPrepare(beefwebTrack), "handoff candidate starts preparing");
        await Task.Delay(200);
        Assert.Null(broadcast.CurrentPlayerData());
        Assert.Equal(BroadcastPhase.Starting, broadcast.BroadcastStatus.Phase);
        Assert.Null(broadcast.BroadcastStatus.LiveProvider);

        gate.Open();
        await TestWait.Assert(
            () => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "beefweb.mp3" &&
                  broadcast.BroadcastStatus.LiveProvider == BroadcastProvider.Beefweb &&
                  broadcast.BroadcastStatus.Phase == BroadcastPhase.Live, "prepared Beefweb provider commits");
    }

    private DirectoryInfo CreateFolder(params string[] tracks)
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "folder"));
        foreach (var t in tracks) TestData.CreateTrack(folder, t);
        return folder;
    }

    private async Task<LocalSource> StartLocalBroadcast(string trackName)
    {
        var folder = CreateFolder(trackName);
        await broadcast.LoadFolder(folder.FullName);
        var player = broadcast.ActiveLocalSource!;
        broadcast.SetOnAir(true);
        player.Play();
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is not null, "local provider starts");
        return player;
    }

    [Fact]
    public async Task Failed_beefweb_activation_leaves_local_stopped_and_reports_failure()
    {
        await StartLocalBroadcast("local.mp3");
        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        await broadcast.SetBeefwebSource(new Watcher(feed, server.CreateClient(), false));
        var broken = Track("broken.mp3");
        service.GateFor(broken).Fail(new InvalidDataException("cannot prepare"));

        feed.Push(new Observation(broken, PulsarState.Playing, TimeSpan.Zero, TimeSpan.FromMinutes(3), "Broken", "DJ",
                                  DateTimeOffset.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp()));

        await TestWait.Assert(() => broadcast.BroadcastStatus.Phase == BroadcastPhase.Retrying, "failure is surfaced");
        Assert.Null(broadcast.CurrentPlayerData());
        Assert.Null(broadcast.BroadcastStatus.LiveProvider);
        Assert.Equal(PulsarState.Stopped, engine.Snapshot.State);
        Assert.Equal(BroadcastProvider.Beefweb, broadcast.BroadcastStatus.DesiredProvider);
    }

    [Fact]
    public async Task Old_provider_loss_during_handoff_emits_a_real_stop_then_stays_armed()
    {
        var player = await StartLocalBroadcast("local.mp3");
        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        await broadcast.SetBeefwebSource(new Watcher(feed, server.CreateClient(), false));
        var next = Track("next.mp3");
        var gate = service.GateFor(next);
        feed.Push(new Observation(next, PulsarState.Playing, TimeSpan.Zero, TimeSpan.FromMinutes(3), "Next", "DJ",
                                  DateTimeOffset.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp()));
        Assert.True(await service.WaitForPrepare(next), "handoff preparation starts");

        player.Stop();
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is null, "old provider stop reaches the wire");
        Assert.True(broadcast.OnAir);

        gate.Open();
        await TestWait.Assert(() => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "next.mp3",
                              "armed On Air publishes the replacement when ready");
    }

    [Fact]
    public async Task LoadFolder_installs_a_playable_local_source()
    {
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);

        Assert.NotNull(broadcast.ActiveLocalSource);
        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true },
                              "the local snapshot goes live through the manager");
    }

    [Fact]
    public async Task Switching_between_folder_and_mod_preserves_local_monitor_playback()
    {
        var folder = CreateFolder("folder.mp3");
        await broadcast.LoadFolder(folder.FullName);
        var player = broadcast.ActiveLocalSource!;
        player.Play();
        await TestWait.Assert(() => engine.Snapshot is { State: PulsarState.Playing, Path: not null },
                              "folder monitor starts");
        var playingPath = engine.Snapshot.Path;

        var mod = Directory.CreateDirectory(Path.Combine(dir.FullName, "mod"));
        TestData.CreateTrack(mod, "mod.mp3");
        resolver.Result = mod.FullName;
        await broadcast.LoadMod("CoolMod");
        await player.DrainForTests();

        Assert.Equal(BroadcastProvider.Local, broadcast.BroadcastStatus.DesiredProvider);
        Assert.Equal(PulsarState.Playing, engine.Snapshot.State);
        Assert.Equal(playingPath, engine.Snapshot.Path);
    }

    [Fact]
    public async Task Natural_track_transition_never_publishes_an_off_air_gap()
    {
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);
        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "a.mp3",
                              "the first track is live");
        while (broadcast.Outputs.TryRead(out _)) { }

        engine.FinishTrack();

        await TestWait.Assert(() => broadcast.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "b.mp3",
                              "the next track replaces it");
        var transitionOutputs = new System.Collections.Generic.List<BroadcastOutput>();
        while (broadcast.Outputs.TryRead(out var output)) transitionOutputs.Add(output);
        Assert.DoesNotContain(transitionOutputs,
                              output => output is BroadcastOutput.PlayerDataChanged { Data: null }
                                            or BroadcastOutput.BroadcastingChanged { Value: false });
    }

    [Fact]
    public async Task Poll_confirmation_starts_broadcasting_when_the_load_event_is_missed()
    {
        engine.SuppressUpdatedEvents = true;
        var folder = CreateFolder("a.mp3");
        await broadcast.LoadFolder(folder.FullName);

        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => engine.Snapshot.State == PulsarState.Playing,
                              "the host starts playing without publishing its change event");
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true },
                              "poll confirmation promotes playback into the broadcast pipeline");
    }

    [Fact]
    public async Task Reconnecting_the_audio_host_restores_local_playback()
    {
        var folder = CreateFolder("a.mp3");
        await broadcast.LoadFolder(folder.FullName);
        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => engine.Snapshot.State == PulsarState.Playing, "local track starts");
        var loads = engine.Ops.Count(op => op == "Load");

        engine.DisconnectTrack();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is null, "disconnect clears the broadcast");
        broadcast.OnEngineReconnected();

        await TestWait.Assert(
            () => engine.Ops.Count(op => op == "Load") == loads + 1 && engine.Snapshot.State == PulsarState.Playing,
            "the selected track reloads after reconnect");
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true },
                              "the reloaded track returns to the broadcast pipeline");
    }

    [Fact]
    public async Task LoadFolder_on_a_missing_directory_installs_nothing()
    {
        await broadcast.LoadFolder(Path.Combine(dir.FullName, "does-not-exist"));
        Assert.NotNull(broadcast.ActiveLocalSource);
        Assert.Empty(broadcast.ActiveLocalSource!.Library);
        Assert.Null(broadcast.CurrentSnapshot);
        Assert.NotNull(broadcast.BrowserLoad.Error);
    }

    [Fact]
    public async Task Local_source_requests_are_applied_in_request_order()
    {
        var older = new GatedCatalogLoader(Path.Combine(dir.FullName, "older"));
        var newer = new GatedCatalogLoader(Path.Combine(dir.FullName, "newer"));
        var olderLoad = broadcast.LoadLocalSource(older, "OlderMod");
        await TestWait.Assert(() => older.Calls == 1, "the older source build starts");
        var newerLoad = broadcast.LoadLocalSource(newer, "NewerMod");
        Assert.Equal(0, newer.Calls);

        TrackCatalog Catalog(GatedCatalogLoader loader, string name)
        {
            var track = new LocalTrack(Path.Combine(loader.RootDirectory, name), name, name);
            return new TrackCatalog([new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [track])]);
        }

        older.Result.TrySetResult(Catalog(older, "old.scd"));
        await TestWait.Within(olderLoad, "the older source installs");
        Assert.Equal("OlderMod", broadcast.ActiveLocalSource?.ModDirectoryName);

        await TestWait.Assert(() => newer.Calls == 1, "the newer source build starts next");
        newer.Result.TrySetResult(Catalog(newer, "new.scd"));
        await TestWait.Within(newerLoad, "the newer source installs");

        Assert.Equal("NewerMod", broadcast.ActiveLocalSource?.ModDirectoryName);
        Assert.Equal(newer.RootDirectory, broadcast.ActiveLocalSource?.RootDirectory);
    }

    [Fact]
    public async Task A_failed_catalog_load_keeps_the_current_source()
    {
        var live = Track("live.flac");
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local,
                                              new FakeMusicSource { Current = TestData.Snap(live) });
        await TestWait.Assert(() => broadcast.CurrentSnapshot?.FilePath == live, "current source starts");
        var failing = new GatedCatalogLoader(Path.Combine(dir.FullName, "broken"));
        failing.Result.TrySetException(new IOException("scan failed"));

        await broadcast.LoadLocalSource(failing, "BrokenMod");

        Assert.Equal(live, broadcast.CurrentSnapshot?.FilePath);
        Assert.Null(broadcast.ActiveLocalSource);
    }

    [Fact]
    public async Task A_browser_only_rescan_does_not_advance_the_playback_cursor()
    {
        // Keep this queue-notification test fast while still going through a timer tick.
        prefetchTiming.LeadMs = int.MaxValue;
        prefetchTiming.FinalMs = int.MaxValue;
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);
        var source = broadcast.ActiveLocalSource!;
        broadcast.SetOnAir(true);
        source.Play();
        await TestWait.Assert(() => broadcast.CurrentPlayerData() is not null, "the playing track is published");
        var epoch = broadcast.CurrentPlayerData()!.Cursor.CursorEpoch;
        File.Delete(Path.Combine(folder.FullName, "b.mp3"));
        TestData.CreateTrack(folder, "c.mp3");

        await source.Rescan();
        Assert.DoesNotContain(source.UpNext, item => item.Track.FilePath.EndsWith("c.mp3"));
        Assert.Equal(epoch, broadcast.CurrentPlayerData()!.Cursor.CursorEpoch);
    }

    [Fact]
    public async Task LoadMod_resolves_the_mod_directory_and_plays()
    {
        resolver.Result = CreateFolder("mod-song.mp3").FullName;
        await broadcast.LoadMod("CoolMod");

        Assert.NotNull(broadcast.ActiveLocalSource);
        broadcast.SetOnAir(true);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true }, "mod local source plays");
    }

    [Fact]
    public async Task LoadMod_failure_keeps_the_current_source_on_air()
    {
        var live = Track("live.flac");
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local,
                                              new FakeMusicSource { Current = TestData.Snap(live) });

        resolver.Result = null; // Penumbra unavailable / mod missing
        await broadcast.LoadMod("MissingMod");

        Assert.Equal(live, broadcast.CurrentSnapshot?.FilePath); // failure must not stop the show
    }
}
