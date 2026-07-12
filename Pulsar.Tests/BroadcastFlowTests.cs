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
    private readonly List<(string, string[], PulsarCursor)?> events = [];
    private readonly List<bool> broadcastingEvents = [];
    private SyncPrep? prep;
    private BroadcastManager? manager;
    private Task? outputPump;
    private volatile Task? outputStall;
    private Action<(string, string[], PulsarCursor)?>? outputProbe;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (manager is not null) await manager.DisposeAsync();
        if (outputPump is not null) await outputPump;
        if (prep is not null) await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private BroadcastManager Create()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        manager = new BroadcastManager(new FakeRemoteEngine(), null!, prep, () => new Configuration());
        outputPump = Task.Run(async () =>
        {
            await foreach (var output in manager.Outputs.ReadAllAsync())
            {
                if (outputStall is { } stall) await stall;
                switch (output)
                {
                    case BroadcastOutput.PlayerDataChanged(var data):
                        outputProbe?.Invoke(data);
                        lock (events) events.Add(data);
                        break;
                    case BroadcastOutput.BroadcastingChanged(var value):
                        lock (broadcastingEvents) broadcastingEvents.Add(value);
                        break;
                }
            }
        });
        return manager;
    }

    private (string, string[], PulsarCursor)?[] Events
    {
        get { lock (events) return [.. events]; }
    }

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static SourceSnapshot Snap(string file, bool playing = true, string? next = null)
        => TestData.Snap(file, playing, next);

    private async Task<FakeMusicSource> StartBroadcasting(BroadcastManager mgr, string track)
    {
        var source = new FakeMusicSource { Current = Snap(track) };
        await mgr.SetSource(source);
        await TestWait.Assert(() => Events.Any(e => e is not null), $"initial manifest for {track}");
        return source;
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
        Assert.Equal(Path.GetFileName(next), manifest!.Value.Item3.Meta!.OriginalFileName);
        Assert.True(manifest.Value.Item3.CursorEpoch > Events[seen - 1]!.Value.Item3.CursorEpoch,
            "track change bumps the cursor epoch");
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
        await TestWait.Assert(() => Events.Length > seen && Events[^1] is null,
            "failed prep emits a stop");
    }

    [Fact]
    public async Task Pause_propagates_immediately_with_a_fresh_epoch()
    {
        var mgr = Create();
        var track = CreateTrack("a.flac");
        var source = await StartBroadcasting(mgr, track);
        var playingManifest = Events[^1]!.Value;

        source.Current = Snap(track, playing: false);
        source.RaiseChanged();

        await TestWait.Assert(
            () => Events[^1] is { } e && !e.Item3.IsPlaying,
            "paused cursor reaches listeners");
        var paused = Events[^1]!.Value;
        Assert.Equal(playingManifest.Item1, paused.Item1); // same synced file
        Assert.True(paused.Item3.CursorEpoch > playingManifest.Item3.CursorEpoch);
    }

    [Fact]
    public async Task Track_change_kicks_a_prefetch_for_the_upcoming_track()
    {
        var mgr = Create();
        var source = await StartBroadcasting(mgr, CreateTrack("a.flac"));

        var current = CreateTrack("b.flac");
        var upcoming = CreateTrack("c.flac");
        source.Current = Snap(current, next: upcoming);
        source.RaiseChanged();

        Assert.True(await service.WaitForPrepare(upcoming),
            "the next track is prepared ahead of time");
    }

    [Fact]
    public async Task Stopping_twice_announces_the_stop_once()
    {
        var mgr = Create();
        await StartBroadcasting(mgr, CreateTrack("a.flac"));
        var seen = Events.Length;

        await mgr.SetSource(null);
        await TestWait.Assert(() => Events.Length == seen + 1 && Events[^1] is null, "stop announced");

        // Stopping while already stopped must not send peers another null.
        await mgr.SetSource(null);
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
            await mgr.SetSource(source);

            var next = CreateTrack("b.flac");
            source.Current = Snap(next);
            source.RaiseChanged();

            prepareStarted = await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(300));
        }
        finally
        {
            outputStall = null;
            resumeOutputs.TrySetResult();
        }

        Assert.True(prepareStarted,
            "preparing the active track is operational work, independent of output consumption");
    }

    [Fact]
    public async Task An_evicted_artifact_is_never_announced()
    {
        var mgr = Create();
        var track = CreateTrack("a.flac");
        var source = await StartBroadcasting(mgr, track);
        var artifact = Events[^1]!.Value.Item1;
        var seen = Events.Length;

        // Check existence AT delivery time: the re-prep recreates the path moments later.
        var ghostsAnnounced = 0;
        outputProbe = e =>
        {
            if (e is { } m && !File.Exists(m.Item1)) Interlocked.Increment(ref ghostsAnnounced);
        };

        // The LRU cache evicts the artifact behind the cached result...
        File.Delete(artifact);
        // ...and the DJ pauses, which recomputes the manifest.
        source.Current = Snap(track, playing: false);
        source.RaiseChanged();

        // The gap must HOLD (like a transcode gap) - never announce a deleted path.
        await TestWait.Assert(
            () => Events.Length > seen && Events[^1] is { } e && !e.Item3.IsPlaying,
            "the re-prepped manifest with the paused cursor");
        Assert.Equal(0, ghostsAnnounced);
    }

    [Fact]
    public async Task Set_source_after_dispose_disposes_the_incoming_source()
    {
        var mgr = Create();
        await mgr.SetSource(new FakeMusicSource { Current = Snap(CreateTrack("a.flac")) });
        await mgr.DisposeAsync();

        // A UI load that lost the race with plugin unload: installing it would leak
        // a live source with no owner left.
        var late = new FakeMusicSource { Current = Snap(CreateTrack("late.flac")) };
        await mgr.SetSource(late);

        Assert.True(late.Disposed, "the incoming source is torn down, not installed");
        Assert.Null(mgr.CurrentSnapshot);
    }

    [Fact]
    public async Task A_late_event_from_the_old_source_cannot_stop_the_broadcast_mid_switch()
    {
        var mgr = Create();

        var teardown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceA = new FakeMusicSource { Current = Snap(CreateTrack("a.flac")), DisposeGate = teardown };
        await mgr.SetSource(sourceA);
        await TestWait.Assert(() => Events.Any(e => e is not null), "initial manifest");
        var seen = Events.Length;

        // A switch between two already-prepped tracks must be seamless: no stop on
        // the wire, no broadcasting drop.
        var trackB = CreateTrack("b.flac");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(trackB));

        // A delivery captured before the switch unsubscribed, still in flight mid-teardown.
        var lateEvent = sourceA.CapturedHandlers!;

        var switching = mgr.SetSource(new FakeMusicSource { Current = Snap(trackB) });
        try
        {
            await TestWait.Assert(() => mgr.CurrentSnapshot is null && !switching.IsCompleted,
                "the switch is parked in the old source's teardown");

            lateEvent(sourceA.Current); // the in-flight delivery lands mid-teardown
        }
        finally
        {
            teardown.TrySetResult(); // always unblock, or fixture teardown deadlocks
        }
        await TestWait.Within(switching, "the switch completes");

        await TestWait.Assert(
            () => Events[^1] is { } e && e.Item3.Meta!.OriginalFileName == "b.flac",
            "the new source's manifest goes out");
        lock (broadcastingEvents) Assert.Equal([true], broadcastingEvents); // one rise at start, never a drop
        Assert.All(Events.Skip(seen), e => Assert.NotNull(e)); // and no stop on the wire
    }

    // Regression test: the transcode-gap HOLD bridged a source switch, so peers never
    // saw the old broadcast stop.
    [Fact]
    public async Task Switching_to_an_unprepared_source_announces_a_stop()
    {
        var mgr = Create();
        var prepped = CreateTrack("a.mp3");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(prepped));

        await mgr.SetSource(new FakeMusicSource { Current = Snap(prepped) });
        await TestWait.Assert(() => Events is [not null, ..], "the prepped track's manifest");

        await mgr.SetSource(new FakeMusicSource { Current = Snap(CreateTrack("b.mp3")) });

        // The switch announces a stop immediately, then the new track once its
        // (here: instant) prep lands.
        await TestWait.Assert(
            () => Events is [_, null, not null, ..],
            "the stop, then the new source's manifest");
    }

    // Regression test: a switch between two already-prepped tracks reused the old cursor
    // epoch, and listeners short-circuit on equal epochs (SyncDecider). The ONLY test
    // protecting the epoch bump - the e2e loopback flow structurally masks epoch reuse.
    //
    // The exact two-event count relies on both tracks being cache-served; if prep
    // caching changes shape, loosen the count before touching the epoch assertion.
    [Fact]
    public async Task Switching_sources_bumps_the_cursor_epoch()
    {
        var mgr = Create();
        var trackA = CreateTrack("a.mp3");
        var trackB = CreateTrack("b.mp3");
        Assert.IsType<PrepResult.Successful>(await prep!.PrepareActive(trackA));
        Assert.IsType<PrepResult.Successful>(await prep.PrepareActive(trackB));

        await mgr.SetSource(new FakeMusicSource { Current = Snap(trackA) });
        await mgr.SetSource(new FakeMusicSource { Current = Snap(trackB) });

        // Wait for both async deliveries, then let the queue settle to prove no third follows.
        await TestWait.Assert(() => Events.Length >= 2, "both manifests delivered");
        await Task.Delay(200);
        var seen = Events;
        Assert.Equal(2, seen.Length);
        Assert.NotNull(seen[0]);
        Assert.NotNull(seen[1]);
        Assert.NotEqual(seen[0]!.Value.Item3.CursorEpoch, seen[1]!.Value.Item3.CursorEpoch);
    }

    [Fact]
    public async Task Switching_sources_during_a_slow_prepare_stops_then_starts_clean()
    {
        var mgr = Create();
        var sourceA = await StartBroadcasting(mgr, CreateTrack("a.flac"));

        // New source whose first track needs a long transcode.
        var slow = CreateTrack("slow.flac");
        var gate = service.GateFor(slow);
        var sourceB = new FakeMusicSource { Current = Snap(slow) };

        await mgr.SetSource(sourceB);
        Assert.True(sourceA.Disposed, "old source is disposed on switch");
        await TestWait.Assert(() => Events[^1] is null, "switch announces a stop while nothing is ready");
        var seen = Events.Length;

        // The new source reports its state; prep is still running.
        sourceB.RaiseChanged();
        Assert.True(await service.WaitForPrepare(slow), "prep started for the new source");
        await Task.Delay(300);
        Assert.Equal(seen, Events.Length); // still stopped, no stale manifest

        gate.Open();

        await TestWait.Assert(
            () => Events[^1] is { } e && e.Item3.Meta!.OriginalFileName == "slow.flac",
            "the new source's manifest goes out once prepared");
    }
}
