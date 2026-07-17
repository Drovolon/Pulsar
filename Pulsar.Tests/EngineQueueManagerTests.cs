using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// EngineQueueManager serializes UI actions into engine RPCs. Volume/seek arrive at
/// slider speed; the contract is that the LAST value always lands, even if
/// intermediate ones get coalesced.
/// </summary>
public class EngineQueueManagerTests
{
    [Fact]
    public async Task A_volume_drag_converges_on_the_final_value()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        for (var i = 0; i <= 9; i++)
            eqm.Volume(i / 10f);

        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.9f) < 0.001f,
            "final slider value reaches the engine");

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task A_seek_scrub_converges_on_the_final_position()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        for (var s = 1; s <= 9; s++)
            eqm.Seek(TimeSpan.FromSeconds(s));

        await TestWait.Assert(
            () => engine.Calls.LastOrDefault(c => c.Op == "Seek")?.Arg is TimeSpan t && t == TimeSpan.FromSeconds(9),
            "final scrub position reaches the engine");

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task Load_and_stop_flow_through_the_queue()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        eqm.Load(@"C:\music\track.mp3", TimeSpan.Zero, startPlaying: true);
        await TestWait.Assert(
            () => engine.Snapshot is { State: NAudio.Wave.PlaybackState.Playing, Path: @"C:\music\track.mp3" },
            "load lands");

        eqm.Stop();
        await TestWait.Assert(() => engine.Snapshot.State == NAudio.Wave.PlaybackState.Stopped, "stop lands");

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task Pause_and_resume_flow_through_the_queue()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        eqm.Load(@"C:\music\track.mp3", TimeSpan.Zero, startPlaying: true);
        eqm.Pause();
        await TestWait.Assert(() => engine.Snapshot.State == NAudio.Wave.PlaybackState.Paused, "pause lands");

        eqm.Resume();
        await TestWait.Assert(() => engine.Snapshot.State == NAudio.Wave.PlaybackState.Playing, "resume lands");

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task A_trigger_happy_transport_burst_is_dispatched_in_order_and_converges()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseLoad.Task : null;

        eqm.Load(@"C:\music\a.mp3", TimeSpan.Zero, startPlaying: true);
        await TestWait.Assert(() => engine.Ops.Count != 0, "the first load parks in the engine");

        eqm.Pause();
        eqm.Resume();
        eqm.Pause();
        eqm.Seek(TimeSpan.FromSeconds(12));
        eqm.Stop();
        eqm.Load(@"C:\music\b.mp3", TimeSpan.Zero, startPlaying: true);
        releaseLoad.TrySetResult();

        await TestWait.Assert(
            () => engine.Snapshot is { State: NAudio.Wave.PlaybackState.Playing, Path: @"C:\music\b.mp3" },
            "the final load wins");
        Assert.Equal(
            ["Load", "Pause", "Resume", "Pause", "Seek", "Stop", "Load"],
            engine.Ops.Take(7));

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task Reapply_volume_only_resends_a_real_value()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        eqm.ReapplyVolume(); // sentinel -1: nothing was ever set, nothing to reapply
        await Task.Delay(150);
        Assert.DoesNotContain("SetVolume", engine.Ops);

        eqm.Volume(0.4f);
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.4f) < 0.001f, "volume lands");

        eqm.ReapplyVolume(); // post-reconnect: the remembered value goes out again
        await TestWait.Assert(() => engine.Ops.Count(o => o == "SetVolume") == 2, "volume re-sent");
        Assert.Equal(0.4f, engine.LastVolume, 3);

        cts.Cancel();
        await eqm.DisposeAsync();
    }

    [Fact]
    public async Task A_failing_command_does_not_kill_the_queue()
    {
        var engine = new FakeRemoteEngine();
        using var cts = new CancellationTokenSource();
        var eqm = new EngineQueueManager(engine, cts.Token);

        engine.Intercept = op => op == "Load" ? new InvalidOperationException("host rejected the load") : null;
        eqm.Load(@"C:\music\bad.mp3", TimeSpan.Zero, startPlaying: true);
        await TestWait.Assert(() => engine.Ops.Contains("Load"), "the failing load was attempted");
        engine.Intercept = null;

        // Per-command isolation: the loop must still dispatch what comes next.
        eqm.Volume(0.7f);
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.7f) < 0.001f,
            "the queue is alive after the failure");

        cts.Cancel();
        await eqm.DisposeAsync();
    }
}
