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
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(engine, resolver, prep, config, prefetchTiming);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await broadcast.DisposeAsync();
        await prep.DisposeAsync();
        dir.Delete(recursive: true);
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
        public TaskCompletionSource<TrackCatalog> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            return Result.Task;
        }
    }

    private string Track(string name) => TestData.CreateTrack(dir, name);

    /// <summary>A snapshot whose duration/next the prefetch timer reads.</summary>
    private static SourceSnapshot TimedSnap(string file, long durationMs, string? next = null, bool playing = true)
        => new(file, next, playing, TimeSpan.Zero, DateTimeOffset.UtcNow,
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

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 1000) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");
        Assert.False(await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(150)),
            "nothing can have prefetched 'next' yet - no event ever carried it");

        // Visible ONLY to the timer: no event is raised.
        source.Current = TimedSnap(current, durationMs: 1000, next: next);

        Assert.True(await service.WaitForPrepare(next), "the lead tick prefetched the next track");
        await TestWait.Assert(
            () => broadcast.CurrentPlayerData()?.PrefetchPath is { Length: > 0 },
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

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 1000) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.Current = TimedSnap(current, durationMs: 1000, next: next); // no event

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
            Current = TimedSnap(current, durationMs: 1000, next: staleNext),
        };
        await broadcast.SetSource(source);

        Assert.True(await service.WaitForPrepare(staleNext), "the checkpoint samples the old queue head");
        source.Current = TimedSnap(current, durationMs: 1000, next: liveNext); // no event
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
        await broadcast.SetSource(source);

        source.Current = TimedSnap(current, 1000, next: next, playing: false); // no event
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

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 0) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.Current = TimedSnap(current, durationMs: 0, next: next); // no event
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
        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 1000) };
        await broadcast.SetSource(source);

        // This observation cancels the playing timer. The next path is installed only
        // after the event, so the paused observation itself cannot prefetch it.
        source.Current = TimedSnap(current, durationMs: 1000, playing: false);
        source.RaiseChanged();
        source.Current = TimedSnap(current, durationMs: 1000, next: staleNext, playing: false);
        await Task.Delay(400); // past the original ~300ms lead checkpoint
        Assert.DoesNotContain(staleNext, service.PrepareCalls);

        source.Current = TimedSnap(current, durationMs: 1000, playing: true);
        source.RaiseChanged();
        source.Current = TimedSnap(current, durationMs: 1000, next: liveNext, playing: true);

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

        var oldSource = new FakeMusicSource { Current = TimedSnap(oldCurrent, durationMs: 1000) };
        await broadcast.SetSource(oldSource);

        var newSource = new FakeMusicSource { Current = TimedSnap(newCurrent, durationMs: 1000) };
        await broadcast.SetSource(newSource); // ClearAsync fences off the old timer
        oldSource.Current = TimedSnap(oldCurrent, durationMs: 1000, next: oldNext);
        newSource.Current = TimedSnap(newCurrent, durationMs: 1000, next: newNext);

        Assert.True(await service.WaitForPrepare(newNext), "the new source owns the checkpoint");
        Assert.DoesNotContain(oldNext, service.PrepareCalls);
    }

    // ---- overlapping switches ---------------------------------------------------

    [Fact]
    public async Task Overlapping_switches_serialize_and_the_last_caller_wins()
    {
        var a = new FakeMusicSource
        {
            Current = TestData.Snap(Track("one.flac")),
            DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await broadcast.SetSource(a);

        var t3 = Track("three.flac");
        var b = new FakeMusicSource { Current = TestData.Snap(Track("two.flac")) };
        var c = new FakeMusicSource { Current = TestData.Snap(t3) };

        var toB = broadcast.SetSource(b); // parks: a's teardown is gated
        await Task.Delay(50);
        Assert.False(toB.IsCompleted, "switch to b waits on a's teardown");
        var toC = broadcast.SetSource(c); // queues behind it in the manager mailbox

        a.DisposeGate.TrySetResult();
        await TestWait.Within(toB, "switch to b");
        await TestWait.Within(toC, "switch to c");

        Assert.True(a.Disposed, "a torn down");
        Assert.True(b.Disposed, "b installed then torn down by c's switch");
        Assert.False(c.Disposed, "c is live");
        Assert.Equal(t3, broadcast.CurrentSnapshot?.FilePath);
    }

    // ---- loaders ------------------------------------------------------------------

    [Fact]
    public async Task Catalog_loading_does_not_block_events_from_the_current_source()
    {
        var first = Track("first.flac");
        var second = Track("second.flac");
        var current = new FakeMusicSource { Current = TestData.Snap(first) };
        await broadcast.SetSource(current);
        var loader = new GatedCatalogLoader(Path.Combine(dir.FullName, "pending"));
        var loading = broadcast.LoadLocalSource(loader);
        await TestWait.Assert(() => loader.Calls == 1, "catalog loading starts");

        current.Current = TestData.Snap(second);
        current.RaiseChanged();

        await TestWait.Assert(
            () => broadcast.CurrentSnapshot?.FilePath == second,
            "the current source remains responsive during the scan");
        loader.Result.TrySetResult(new TrackCatalog(
            [new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [])]));
        await TestWait.Within(loading, "the catalog load finishes");
    }

    [Fact]
    public async Task Beefweb_stays_off_air_until_the_session_gate_is_enabled()
    {
        var feed = new ControllableFeed();
        var server = new FakeBeefwebServer();
        var watcher = new Watcher(feed, server.CreateClient(), isWine: false, config, onAir: false);
        await broadcast.SetSource(watcher);
        var track = Track("paused.flac");

        feed.Push(new Observation(
            track,
            PulsarState.Paused,
            TimeSpan.FromSeconds(12),
            TimeSpan.FromMinutes(3),
            "Paused Song",
            "Band",
            DateTimeOffset.UtcNow,
            System.Diagnostics.Stopwatch.GetTimestamp()));

        await TestWait.Assert(() => watcher.Observed is not null, "Beefweb observes its initial state");
        Assert.False(broadcast.BeefwebOnAir);
        Assert.Null(broadcast.CurrentSnapshot);
        Assert.Null(broadcast.CurrentPlayerData());

        broadcast.SetBeefwebOnAir(true);

        await TestWait.Assert(() => broadcast.CurrentSnapshot is not null, "the session gate opens");
        Assert.True(broadcast.BeefwebOnAir);
        Assert.Equal(track, broadcast.CurrentSnapshot!.FilePath);
        Assert.False(broadcast.CurrentSnapshot.IsPlaying);

        broadcast.SetBeefwebOnAir(false);

        await TestWait.Assert(() => broadcast.CurrentSnapshot is null, "the session gate closes");
        Assert.False(broadcast.BeefwebOnAir);
        Assert.Null(broadcast.CurrentPlayerData());
    }

    private DirectoryInfo CreateFolder(params string[] tracks)
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "folder"));
        foreach (var t in tracks) TestData.CreateTrack(folder, t);
        return folder;
    }

    [Fact]
    public async Task LoadFolder_installs_a_playable_local_source()
    {
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);

        Assert.NotNull(broadcast.ActiveLocalSource);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true },
            "the local snapshot goes live through the manager");
    }

    [Fact]
    public async Task Poll_confirmation_starts_broadcasting_when_the_load_event_is_missed()
    {
        engine.SuppressChangedEvents = true;
        var folder = CreateFolder("a.mp3");
        await broadcast.LoadFolder(folder.FullName);

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
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => engine.Snapshot.State == PulsarState.Playing, "local track starts");
        var loads = engine.Ops.Count(op => op == "Load");

        engine.DisconnectTrack();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is null, "disconnect clears the broadcast");
        broadcast.OnEngineReconnected();

        await TestWait.Assert(
            () => engine.Ops.Count(op => op == "Load") == loads + 1
                  && engine.Snapshot.State == PulsarState.Playing,
            "the selected track reloads after reconnect");
        Assert.NotNull(broadcast.CurrentSnapshot);
    }

    [Fact]
    public async Task LoadFolder_on_a_missing_directory_installs_nothing()
    {
        await broadcast.LoadFolder(Path.Combine(dir.FullName, "does-not-exist"));
        Assert.Null(broadcast.ActiveLocalSource);
        Assert.Null(broadcast.CurrentSnapshot);
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
        await broadcast.SetSource(new FakeMusicSource { Current = TestData.Snap(live) });
        var failing = new GatedCatalogLoader(Path.Combine(dir.FullName, "broken"));
        failing.Result.TrySetException(new IOException("scan failed"));

        await broadcast.LoadLocalSource(failing, "BrokenMod");

        Assert.Equal(live, broadcast.CurrentSnapshot?.FilePath);
        Assert.Null(broadcast.ActiveLocalSource);
    }

    [Fact]
    public async Task A_queue_only_rescan_does_not_advance_the_playback_cursor()
    {
        // Keep this queue-notification test fast while still going through a timer tick.
        prefetchTiming.LeadMs = int.MaxValue;
        prefetchTiming.FinalMs = int.MaxValue;
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);
        var source = broadcast.ActiveLocalSource!;
        source.Play();
        await TestWait.Assert(
            () => broadcast.CurrentPlayerData() is not null,
            "the playing track is published");
        var epoch = broadcast.CurrentPlayerData()!.Cursor.CursorEpoch;
        File.Delete(Path.Combine(folder.FullName, "b.mp3"));
        var newNext = TestData.CreateTrack(folder, "c.mp3");

        await source.Rescan();
        Assert.True(await service.WaitForPrepare(newNext), "the new queue reaches prefetch");

        Assert.Equal(epoch, broadcast.CurrentPlayerData()!.Cursor.CursorEpoch);
    }

    [Fact]
    public async Task LoadMod_resolves_the_mod_directory_and_plays()
    {
        resolver.Result = CreateFolder("mod-song.mp3").FullName;
        await broadcast.LoadMod("CoolMod");

        Assert.NotNull(broadcast.ActiveLocalSource);
        broadcast.ActiveLocalSource!.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true }, "mod local source plays");
    }

    [Fact]
    public async Task LoadMod_failure_keeps_the_current_source_on_air()
    {
        var live = Track("live.flac");
        await broadcast.SetSource(new FakeMusicSource { Current = TestData.Snap(live) });

        resolver.Result = null; // Penumbra unavailable / mod missing
        await broadcast.LoadMod("MissingMod");

        Assert.Equal(live, broadcast.CurrentSnapshot?.FilePath); // failure must not stop the show
    }
}
