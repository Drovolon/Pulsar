using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Prepare;
using Pulsar.Ipc;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Manifest-lifecycle scenarios for BroadcastManager: what listeners are told while
/// transcodes are in flight, fail, or the DJ pauses/switches tracks. The load-bearing
/// invariant: null means a real stop; a transcode gap silently HOLDS the last manifest.
/// </summary>
public class BroadcastFlowTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-bf-test-");
    private readonly ControllablePrepareService service = new();
    private readonly List<BroadcastPlayerData?> events = [];
    private readonly List<bool> broadcastingEvents = [];
    private readonly List<bool> broadcastAudioEvents = [];
    private SyncPrep? prep;
    private BroadcastManager? manager;
    private Task? outputPump;
    private volatile Task? outputStall;
    private Action<BroadcastPlayerData?>? outputProbe;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (manager is not null) await manager.DisposeAsync();
        if (outputPump is not null) await outputPump;
        if (prep is not null) await prep.DisposeAsync();
        dir.Delete(true);
    }

    private BroadcastManager Create(
        PrefetchTiming? prefetchTiming = null, BroadcastRetryTiming? retryTiming = null, long? cacheCapBytes = null,
        TimeSpan? providerDisposeTimeout = null)
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache"))
        {
            CacheCapBytes = cacheCapBytes ?? 1L << 26,
        }, service);
        manager = new BroadcastManager(new FakeRemoteEngine(), null!, prep, new Configuration(),
                                       prefetchTiming ?? new PrefetchTiming(), retryTiming, providerDisposeTimeout);
        outputPump = Task.Run(async () =>
        {
            await foreach (var output in manager.Outputs.ReadAllAsync())
            {
                if (outputStall is { } stall) await stall;
                switch (output)
                {
                    case BroadcastOutput.PlayerDataChanged change:
                        outputProbe?.Invoke(change.Data);
                        lock (events)
                        {
                            events.Add(change.Data);
                        }

                        change.Dispose();
                        break;
                    case BroadcastOutput.BroadcastingChanged(var value):
                        lock (broadcastingEvents)
                        {
                            broadcastingEvents.Add(value);
                        }

                        break;
                    case BroadcastOutput.BroadcastAudioChanged(var value):
                        lock (broadcastAudioEvents)
                        {
                            broadcastAudioEvents.Add(value);
                        }

                        break;
                }
            }
        });
        return manager;
    }

    private BroadcastPlayerData?[] Events
    {
        get
        {
            lock (events)
            {
                return [.. events];
            }
        }
    }

    private bool[] BroadcastAudioEvents
    {
        get
        {
            lock (broadcastAudioEvents)
            {
                return [.. broadcastAudioEvents];
            }
        }
    }

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static SourceSnapshot Snap(string file, bool playing = true, string? next = null) =>
        TestData.Snap(file, playing, next);

    private async Task<FakeMusicSource> StartBroadcasting(BroadcastManager mgr, string track)
    {
        var source = new FakeMusicSource { Current = Snap(track) };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, source);
        await TestWait.Assert(() => Events.Any(e => e is not null), $"initial manifest for {track}");
        return source;
    }

    [Fact]
    public async Task Private_local_monitor_keeps_broadcast_audio_active_across_on_air_toggle()
    {
        var mgr = Create();
        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "local-monitor"));
        TestData.CreateTrack(folder, "local.flac");
        await mgr.LoadFolder(folder.FullName);
        var source = mgr.ActiveLocalSource!;

        source.Play();
        await TestWait.Assert(() => BroadcastAudioEvents is [true], "private monitor starts broadcast-side audio");
        Assert.False(mgr.OnAir);
        Assert.Null(mgr.CurrentPlayerData());

        mgr.SetOnAir(true);
        await TestWait.Assert(() => Events.LastOrDefault() is not null, "monitor goes on air");
        mgr.SetOnAir(false);
        await TestWait.Assert(() => Events.LastOrDefault() is null, "publication stops");
        Assert.Equal([true], BroadcastAudioEvents);

        source.Stop();
        await TestWait.Assert(() => BroadcastAudioEvents is [true, false],
                              "stopping the monitor clears broadcast audio");
    }

    [Fact]
    public async Task Track_change_holds_the_manifest_until_the_new_track_is_prepared()
    {
        var mgr = Create();
        var source = await StartBroadcasting(mgr, CreateTrack("a.flac"));
        var seen = Events.Length;

        // DJ's player advances to an untranscoded track: prep takes a while.
        var next = CreateTrack("b.flac");
        var gate = service.GateFor(next);
        source.Current = Snap(next);
        source.RaiseChanged();

        // Listeners must keep the previous manifest while prep runs - a null drops everyone.
        Assert.True(await service.WaitForPrepare(next), "prep started");
        await Task.Delay(300);
        Assert.Equal(seen, Events.Length);

        gate.Open();

        await TestWait.Assert(() => Events.Length > seen, "manifest emitted once prep finished");
        var manifest = Events[^1];
        Assert.NotNull(manifest);
        Assert.Equal(Path.GetFileName(next), manifest!.Cursor.Meta!.OriginalFileName);
        Assert.True(manifest.Cursor.CursorEpoch > Events[seen - 1]!.Cursor.CursorEpoch,
                    "track change bumps the cursor epoch");
    }

    [Fact]
    public async Task A_superseded_track_prepare_cannot_publish_after_the_dj_moves_again()
    {
        var mgr = Create();
        var first = CreateTrack("a.flac");
        var source = await StartBroadcasting(mgr, first);
        var seen = Events.Length;

        var skipped = CreateTrack("b.flac");
        var skippedGate = service.GateFor(skipped);
        source.Current = Snap(skipped);
        source.RaiseChanged();
        Assert.True(await service.WaitForPrepare(skipped), "the skipped track starts preparing");

        // The DJ changes their mind before B is ready. A is cache-served, so it can
        // be announced immediately while B's canceled completion is still in flight.
        source.Current = Snap(first);
        source.RaiseChanged();
        await TestWait.Assert(
            () => Events.Length > seen && Events[^1] is { } e && e.Cursor.Meta!.OriginalFileName == "a.flac",
            "the return to A is published");

        skippedGate.Open();
        await Task.Delay(200); // let any stale PrepCompleted message reach the mailbox

        Assert.All(Events.Skip(seen), e => Assert.NotEqual("b.flac", e?.Cursor.Meta?.OriginalFileName));
        Assert.Equal("a.flac", Events[^1]!.Cursor.Meta!.OriginalFileName);
    }

    [Fact]
    public async Task Prep_failure_announces_a_stop()
    {
        var mgr = Create();
        var source = await StartBroadcasting(mgr, CreateTrack("a.flac"));
        var seen = Events.Length;

        var broken = CreateTrack("broken.flac");
        service.GateFor(broken).Fail(new InvalidDataException("codec exploded"));
        source.Current = Snap(broken);
        source.RaiseChanged();

        // The broadcast must stop when the track is unpreparable.
        await TestWait.Assert(() => Events.Length > seen && Events[^1] is null, "failed prep emits a stop");
    }

    [Fact]
    public async Task Pause_propagates_immediately_with_a_fresh_epoch()
    {
        var mgr = Create();
        var track = CreateTrack("a.flac");
        var source = await StartBroadcasting(mgr, track);
        var playingManifest = Events[^1]!;

        source.Current = Snap(track, false);
        source.RaiseChanged();

        await TestWait.Assert(() => Events[^1] is { } e && !e.Cursor.IsPlaying, "paused cursor reaches listeners");
        var paused = Events[^1]!;
        Assert.Equal(playingManifest.CurrentPath, paused.CurrentPath); // same synced file
        Assert.True(paused.Cursor.CursorEpoch > playingManifest.Cursor.CursorEpoch);
    }

    [Fact]
    public async Task Scheduled_prefetch_republishes_the_same_cursor_with_the_upcoming_file()
    {
        // TestData.Snap is 5s into a 60s track: the 54.8s lead schedules a tick
        // about 200ms from now.
        var prefetchTiming = new PrefetchTiming { LeadMs = 54_800, FinalMs = 100 };
        var mgr = Create(prefetchTiming);
        var source = await StartBroadcasting(mgr, CreateTrack("a.flac"));

        var current = CreateTrack("b.flac");
        var upcoming = CreateTrack("c.flac");
        source.Current = Snap(current, next: upcoming);
        source.RaiseChanged();

        await TestWait.Assert(() => Events[^1]?.Cursor.Meta?.OriginalFileName == "b.flac",
                              "the current track manifest is ready");
        var withoutPrefetch = Events[^1]!;
        Assert.False(await service.WaitForPrepare(upcoming, TimeSpan.FromMilliseconds(100)),
                     "switching tracks does not immediately start the prefetch");

        Assert.True(await service.WaitForPrepare(upcoming), "the lead checkpoint prepares the upcoming track");
        await TestWait.Assert(() => Events[^1] is { PrefetchPath.Length: > 0 },
                              "the prepared upcoming track is republished");

        var withPrefetch = Events[^1]!;
        Assert.Equal(withoutPrefetch.CurrentPath, withPrefetch.CurrentPath);
        Assert.Equal(withoutPrefetch.Cursor, withPrefetch.Cursor);
        Assert.NotEqual(withPrefetch.CurrentPath, withPrefetch.PrefetchPath);
    }

    [Fact]
    public async Task Stopping_twice_announces_the_stop_once()
    {
        var mgr = Create();
        await StartBroadcasting(mgr, CreateTrack("a.flac"));
        var seen = Events.Length;

        mgr.SetOnAir(false);
        await TestWait.Assert(() => Events.Length == seen + 1 && Events[^1] is null, "stop announced");

        // Stopping while already stopped must not send peers another null.
        mgr.SetOnAir(false);
        await Task.Delay(200);
        Assert.Equal(seen + 1, Events.Length);
    }

    [Fact]
    public async Task Broadcast_progress_does_not_depend_on_output_consumption()
    {
        var mgr = Create();
        var initial = CreateTrack("a.flac");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(initial));

        var resumeOutputs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outputStall = resumeOutputs.Task;

        var source = new FakeMusicSource { Current = Snap(initial) };
        var prepareStarted = false;
        try
        {
            await mgr.BroadcastFromForTests(BroadcastProvider.Local, source);

            var next = CreateTrack("b.flac");
            source.Current = Snap(next);
            source.RaiseChanged();

            prepareStarted = await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(300));
        } finally
        {
            outputStall = null;
            resumeOutputs.TrySetResult();
        }

        Assert.True(prepareStarted, "preparing the active track is operational work, independent of output consumption");
    }

    [Fact]
    public async Task An_evicted_artifact_is_never_announced()
    {
        var mgr = Create();
        var track = CreateTrack("a.flac");
        var source = await StartBroadcasting(mgr, track);
        var artifact = Events[^1]!.CurrentPath;
        var seen = Events.Length;

        // Check existence AT delivery time: the re-prep recreates the path moments later.
        var ghostsAnnounced = 0;
        outputProbe = e =>
        {
            if (e is { } m && !File.Exists(m.CurrentPath)) Interlocked.Increment(ref ghostsAnnounced);
        };

        // The LRU cache evicts the artifact behind the cached result...
        File.Delete(artifact);
        // ...and the DJ pauses, which recomputes the manifest.
        source.Current = Snap(track, false);
        source.RaiseChanged();

        // The gap must HOLD (like a transcode gap) - never announce a deleted path.
        await TestWait.Assert(() => Events.Length > seen && Events[^1] is { } e && !e.Cursor.IsPlaying,
                              "the re-prepped manifest with the paused cursor");
        Assert.Equal(0, ghostsAnnounced);
    }

    [Fact]
    public async Task Queued_publications_keep_their_artifacts_pinned_until_delivery()
    {
        service.ArtifactBytes = 600;
        var mgr = Create(cacheCapBytes: 1000);
        var releaseOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outputStall = releaseOutput.Task;
        try
        {
            var first = CreateTrack("first.flac");
            await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(first) });
            await TestWait.Assert(() => mgr.CurrentSnapshot?.FilePath == first, "first commits");
            Assert.True(prep!.TryGet(first, out var firstResult));
            var firstArtifact = Assert.IsType<PrepResult.Successful>(firstResult).PreparedFilePath;

            var second = CreateTrack("second.flac");
            await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(second) });
            await TestWait.Assert(() => mgr.CurrentSnapshot?.FilePath == second, "second commits");

            var pressure = CreateTrack("pressure.flac");
            Assert.IsType<PrepResult.Successful>(await prep.PreparePrefetch(pressure));
            Assert.True(File.Exists(firstArtifact), "the stalled first publication still owns its artifact");
        } finally
        {
            outputStall = null;
            releaseOutput.TrySetResult();
        }
    }

    [Fact]
    public async Task Provider_replacement_after_dispose_disposes_the_incoming_source()
    {
        var mgr = Create();
        await mgr.BroadcastFromForTests(BroadcastProvider.Local,
                                        new FakeMusicSource { Current = Snap(CreateTrack("a.flac")) });
        await mgr.DisposeAsync();

        // A UI load that lost the race with plugin unload: installing it would leak
        // a live source with no owner left.
        var late = new FakeMusicSource { Current = Snap(CreateTrack("late.flac")) };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, late);

        Assert.True(late.Disposed, "the incoming source is torn down, not installed");
        Assert.Null(mgr.CurrentSnapshot);
    }

    [Fact]
    public async Task A_late_event_from_the_old_source_cannot_stop_the_broadcast_mid_switch()
    {
        var mgr = Create();

        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceA = new FakeMusicSource { Current = Snap(CreateTrack("a.flac")), DisposeGate = teardown };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, sourceA);
        await TestWait.Assert(() => Events.Any(e => e is not null), "initial manifest");
        var seen = Events.Length;

        // A switch between two already-prepped tracks must be seamless: no stop on
        // the wire, no broadcasting drop.
        var trackB = CreateTrack("b.flac");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(trackB));

        // A delivery captured before the switch unsubscribed, still in flight mid-teardown.
        var lateEvent = sourceA.CapturedHandlers!;

        var switching = mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(trackB) });
        try
        {
            await TestWait.Within(switching, "the replacement is installed");
            await TestWait.Assert(() => mgr.CurrentSnapshot?.FilePath == trackB, "the prepared replacement commits");
            lateEvent(null); // a delivery captured before unsubscribe lands after commit
        } finally
        {
            teardown.TrySetResult(); // always unblock, or fixture teardown deadlocks
        }

        await TestWait.Within(switching, "the switch completes");

        await TestWait.Assert(() => Events[^1] is { } e && e.Cursor.Meta!.OriginalFileName == "b.flac",
                              "the new source's manifest goes out");
        lock (broadcastingEvents)
        {
            Assert.Equal([true], broadcastingEvents); // one rise at start, never a drop
        }

        Assert.All(Events.Skip(seen), e => Assert.NotNull(e)); // and no stop on the wire
    }

    [Fact]
    public async Task Switching_to_an_unprepared_source_holds_the_committed_broadcast()
    {
        var mgr = Create();
        var prepped = CreateTrack("a.mp3");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(prepped));

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(prepped) });
        await TestWait.Assert(() => Events is [not null, ..], "the prepped track's manifest");

        var next = CreateTrack("b.mp3");
        var gate = service.GateFor(next);
        var seen = Events.Length;
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(next) });

        Assert.True(await service.WaitForPrepare(next), "replacement preparation starts");
        await Task.Delay(200);
        Assert.Equal(seen, Events.Length);
        Assert.Equal("a.mp3", Events[^1]!.Cursor.Meta!.OriginalFileName);

        gate.Open();
        await TestWait.Assert(() => Events[^1]?.Cursor.Meta?.OriginalFileName == "b.mp3",
                              "the prepared replacement commits without a stop");
        Assert.All(Events, Assert.NotNull);
    }

    // Regression test: a switch between two already-prepped tracks reused the old cursor
    // epoch, and listeners short-circuit on equal epochs (SyncDecider). The ONLY test
    // protecting the epoch bump - the e2e loopback flow structurally masks epoch reuse.
    //
    // The two-event count relies on both tracks being cache-served; if prep
    // caching changes shape, loosen the count before touching the epoch assertion.
    [Fact]
    public async Task Switching_sources_bumps_the_cursor_epoch()
    {
        var mgr = Create();
        var trackA = CreateTrack("a.mp3");
        var trackB = CreateTrack("b.mp3");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(trackA));
        Assert.IsType<PrepResult.Successful>(await prep.PrepareActive(trackB));

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(trackA) });
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(trackB) });

        // Wait for both async deliveries, then let the queue settle to prove no third follows.
        await TestWait.Assert(() => Events.Length >= 2, "both manifests delivered");
        await Task.Delay(200);
        var seen = Events;
        Assert.Equal(2, seen.Length);
        Assert.NotNull(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.NotEqual(seen[0]!.Cursor.CursorEpoch, seen[1]!.Cursor.CursorEpoch);
    }

    [Fact]
    public async Task Returning_to_the_same_local_provider_after_beefweb_is_a_new_activation()
    {
        var mgr = Create();
        var localTrack = CreateTrack("local.flac");
        var beefwebTrack = CreateTrack("beefweb.flac");
        var local = new FakeMusicSource { Current = Snap(localTrack) };
        var beefweb = new FakeMusicSource { Current = Snap(beefwebTrack) };

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, local);
        await TestWait.Assert(() => Events.LastOrDefault()?.Cursor.Meta?.OriginalFileName == "local.flac",
                              "local activation commits");
        var firstLocal = Events[^1]!;

        await mgr.InstallProviderForTests(BroadcastProvider.Beefweb, beefweb);
        mgr.SetProvider(BroadcastProvider.Beefweb);
        await TestWait.Assert(() => Events.LastOrDefault()?.Cursor.Meta?.OriginalFileName == "beefweb.flac",
                              "beefweb activation commits");
        var beefwebManifest = Events[^1]!;

        mgr.SetProvider(BroadcastProvider.Local);
        await TestWait.Assert(
            () => Events.LastOrDefault() is { } latest &&
                  latest.Cursor.Meta?.OriginalFileName == "local.flac" &&
                  latest.Cursor.CursorEpoch != firstLocal.Cursor.CursorEpoch,
            "the unchanged local cursor is activated again");

        var secondLocal = Events[^1]!;
        Assert.NotEqual(beefwebManifest.Cursor.CursorEpoch, secondLocal.Cursor.CursorEpoch);
        Assert.Equal(firstLocal.CurrentPath, secondLocal.CurrentPath);
        Assert.DoesNotContain(Events, static value => value is null);
    }

    [Fact]
    public async Task Switching_sources_during_a_slow_prepare_keeps_the_old_source_live()
    {
        var mgr = Create();
        var sourceA = await StartBroadcasting(mgr, CreateTrack("a.flac"));

        // New source whose first track needs a long transcode.
        var slow = CreateTrack("slow.flac");
        var gate = service.GateFor(slow);
        var sourceB = new FakeMusicSource { Current = Snap(slow) };

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, sourceB);
        var seen = Events.Length;

        // The new source reports its state; prep is still running.
        sourceB.RaiseChanged();
        Assert.True(await service.WaitForPrepare(slow), "prep started for the new source");
        await Task.Delay(300);
        Assert.Equal(seen, Events.Length);
        Assert.False(sourceA.Disposed, "the current provider stays alive during handoff");

        gate.Open();

        await TestWait.Assert(() => Events[^1] is { } e && e.Cursor.Meta!.OriginalFileName == "slow.flac",
                              "the new source's manifest goes out once prepared");
        await TestWait.Assert(() => sourceA.Disposed, "the old provider retires after commit");
        Assert.All(Events, Assert.NotNull);
    }

    [Fact]
    public async Task Live_provider_advance_preempts_a_pending_handoff_then_handoff_resumes()
    {
        var mgr = Create();
        var first = CreateTrack("first.flac");
        var sourceA = await StartBroadcasting(mgr, first);

        var replacementTrack = CreateTrack("replacement.flac");
        var replacementGate = service.GateFor(replacementTrack);
        var sourceB = new FakeMusicSource { Current = Snap(replacementTrack) };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, sourceB);
        Assert.True(await service.WaitForPrepare(replacementTrack), "handoff preparation starts");

        Assert.Equal(first, mgr.CurrentSnapshot?.FilePath);
        Assert.Equal("first.flac", mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName);

        var advanced = CreateTrack("advanced.flac");
        sourceA.Current = Snap(advanced);
        sourceA.RaiseChanged();

        await TestWait.Assert(
            () => mgr.CurrentSnapshot?.FilePath == advanced &&
                  mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "advanced.flac",
            "the current provider remains live while handoff is pending");
        Assert.False(sourceA.Disposed);

        replacementGate.Open();
        await TestWait.Assert(
            () => mgr.CurrentSnapshot?.FilePath == replacementTrack &&
                  mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName == "replacement.flac",
            "the desired-provider handoff resumes after the live update commits");
        await TestWait.Assert(() => sourceA.Disposed, "the old provider retires after handoff");
        Assert.All(Events, Assert.NotNull);
    }

    [Fact]
    public async Task A_fresh_same_path_observation_retries_a_failed_handoff()
    {
        var mgr = Create();
        await StartBroadcasting(mgr, CreateTrack("live.flac"));
        var candidatePath = CreateTrack("candidate.flac");
        service.GateFor(candidatePath).Fail(new IOException("temporary failure"));
        var candidateSource = new FakeMusicSource { Current = Snap(candidatePath) };

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, candidateSource);
        await TestWait.Assert(() => mgr.BroadcastStatus.Phase == BroadcastPhase.Retrying, "failure is surfaced");
        Assert.Equal("live.flac", mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName);

        service.Regate(candidatePath).Open();
        candidateSource.RaiseChanged();

        await TestWait.Assert(
            () => mgr.CurrentSnapshot?.FilePath == candidatePath && mgr.BroadcastStatus.Phase == BroadcastPhase.Live,
            "a new observation revision retries the same path");
        Assert.True(service.PrepareCalls.Count(path => path == candidatePath) >= 2);
    }

    [Fact]
    public async Task Failed_handoff_has_a_bounded_retry_budget_and_explicit_restart()
    {
        var mgr = Create(retryTiming: new BroadcastRetryTiming
        {
            Delays =
            [
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(30),
            ],
        });
        await StartBroadcasting(mgr, CreateTrack("live.flac"));
        var broken = CreateTrack("broken.flac");
        service.GateFor(broken).Fail(new IOException("still broken"));

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(broken) });
        await TestWait.Assert(() => service.PrepareCalls.Count(path => path == broken) == 4,
                              "initial attempt plus the 1/2/5 retry budget");
        await Task.Delay(100);
        Assert.Equal(4, service.PrepareCalls.Count(path => path == broken));
        Assert.Equal(BroadcastPhase.Failed, mgr.BroadcastStatus.Phase);
        Assert.Equal("live.flac", mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName);

        service.Regate(broken).Open();
        mgr.RetryHandoff();
        await TestWait.Assert(
            () => mgr.CurrentSnapshot?.FilePath == broken && mgr.BroadcastStatus.Phase == BroadcastPhase.Live,
            "explicit retry creates a fresh bounded retry session");
    }

    [Fact]
    public async Task Cursor_change_retargets_the_running_retry_without_resetting_its_budget()
    {
        var mgr = Create(retryTiming: new BroadcastRetryTiming
        {
            Delays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)],
        });
        await StartBroadcasting(mgr, CreateTrack("live.flac"));
        var candidatePath = CreateTrack("candidate.flac");
        service.GateFor(candidatePath).Fail(new IOException("first attempt"));
        var candidate = new FakeMusicSource { Current = Snap(candidatePath) };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, candidate);
        await TestWait.Assert(() => service.PrepareCalls.Count(path => path == candidatePath) >= 1, "first attempt fails");

        var retryGate = service.Regate(candidatePath);
        await TestWait.Assert(() => service.PrepareCalls.Count(path => path == candidatePath) >= 2,
                              "bounded retry is running");
        candidate.Current = Snap(candidatePath, false);
        candidate.RaiseChanged();
        retryGate.Open();

        await TestWait.Assert(() => mgr.CurrentSnapshot is { FilePath: var path, IsPlaying: false } && path == candidatePath,
                              "the retry result commits the latest cursor");
        Assert.Equal(2, service.PrepareCalls.Count(path => path == candidatePath));
    }

    [Fact]
    public async Task Missing_input_exhausts_instead_of_retrying_forever()
    {
        var mgr = Create(retryTiming: new BroadcastRetryTiming
        {
            Delays =
            [
                TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20),
                TimeSpan.FromMilliseconds(30),
            ],
        });
        await StartBroadcasting(mgr, CreateTrack("live.flac"));
        var missing = Path.Combine(dir.FullName, "missing.flac");

        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(missing) });
        await TestWait.Assert(() => mgr.BroadcastStatus.Phase == BroadcastPhase.Failed, "retry budget exhausts");
        await Task.Delay(100);

        Assert.Equal(BroadcastPhase.Failed, mgr.BroadcastStatus.Phase);
        Assert.DoesNotContain(missing, service.PrepareCalls);
        Assert.Equal("live.flac", mgr.CurrentPlayerData()?.Cursor.Meta?.OriginalFileName);
    }

    [Fact]
    public async Task Failed_live_refresh_does_not_abandon_the_pending_handoff()
    {
        var mgr = Create(retryTiming: new BroadcastRetryTiming
        {
            Delays = [TimeSpan.FromMilliseconds(10)],
        });
        var live = await StartBroadcasting(mgr, CreateTrack("live.flac"));
        var desiredPath = CreateTrack("desired.flac");
        var desiredGate = service.GateFor(desiredPath);
        await mgr.InstallProviderForTests(BroadcastProvider.Beefweb, new FakeMusicSource { Current = Snap(desiredPath) });
        mgr.SetProvider(BroadcastProvider.Beefweb);
        Assert.True(await service.WaitForPrepare(desiredPath), "handoff prep starts");

        var brokenLive = CreateTrack("broken-live.flac");
        service.GateFor(brokenLive).Fail(new IOException("live refresh failed"));
        live.Current = Snap(brokenLive);
        live.RaiseChanged();
        await TestWait.Assert(() => mgr.CurrentPlayerData() is null, "stale live assertion stops");

        desiredGate.Open();
        await TestWait.Assert(() => mgr.CurrentSnapshot?.FilePath == desiredPath,
                              "desired handoff resumes without another provider event");
    }

    [Fact]
    public async Task Disposal_awaits_providers_retired_by_a_handoff()
    {
        var mgr = Create();
        var retirementGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new FakeMusicSource
        {
            Current = Snap(CreateTrack("old.flac")),
            DisposeGate = retirementGate,
        };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, old);
        await TestWait.Assert(() => mgr.CurrentPlayerData() is not null, "old provider commits");

        var replacement = CreateTrack("replacement.flac");
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = Snap(replacement) });
        await TestWait.Assert(() => mgr.CurrentSnapshot?.FilePath == replacement, "replacement commits");

        var disposing = mgr.DisposeAsync().AsTask();
        await Task.Delay(100);
        Assert.False(disposing.IsCompleted, "shutdown drains retired provider disposal");

        retirementGate.TrySetResult();
        await TestWait.Within(disposing, "manager disposal");
        Assert.True(old.Disposed);
    }

    [Fact]
    public async Task Hung_provider_retirement_does_not_block_manager_shutdown_forever()
    {
        var mgr = Create(providerDisposeTimeout: TimeSpan.FromMilliseconds(50));
        var retirementGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new FakeMusicSource
        {
            Current = Snap(CreateTrack("old.flac")),
            DisposeGate = retirementGate,
        };
        await mgr.BroadcastFromForTests(BroadcastProvider.Local, old);
        await mgr.BroadcastFromForTests(BroadcastProvider.Local,
                                        new FakeMusicSource { Current = Snap(CreateTrack("replacement.flac")) });

        await TestWait.Within(mgr.DisposeAsync().AsTask(), "bounded provider retirement");
        Assert.False(old.Disposed);

        retirementGate.TrySetResult();
        await TestWait.Assert(() => old.Disposed, "late physical disposal is still observed");
    }
}
