using System;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// EngineSession is the single ordering and confirmation boundary around an engine.
/// These tests exercise the imperative RPC stream produced from declarative targets.
/// </summary>
public class EngineSessionTests
{
    private static EngineTarget Target(long revision, string path, PlaybackState state)
        => new(revision, path, state, TimeSpan.Zero);

    [Fact]
    public async Task A_volume_drag_converges_on_the_final_value()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);

        for (var i = 0; i <= 9; i++) session.SetVolume(i / 10f);

        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.9f) < 0.001f,
            "final slider value reaches the engine");
    }

    [Fact]
    public async Task A_seek_scrub_converges_on_the_final_position()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);

        for (var s = 1; s <= 9; s++) session.Seek(TimeSpan.FromSeconds(s));

        await TestWait.Assert(
            () => engine.Calls.LastOrDefault(c => c.Op == "Seek")?.Arg is TimeSpan t
                  && t == TimeSpan.FromSeconds(9),
            "final scrub position reaches the engine");
    }

    [Fact]
    public async Task Load_and_stop_flow_through_the_session()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);

        session.SetTarget(Target(1, @"C:\music\track.mp3", PlaybackState.Playing));
        await TestWait.Assert(
            () => engine.Snapshot is { State: PlaybackState.Playing, Path: @"C:\music\track.mp3" },
            "load lands");

        await session.StopAsync();
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Stopped, "stop lands");
    }

    [Fact]
    public async Task Pause_and_resume_flow_through_the_session()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);
        var path = @"C:\music\track.mp3";

        session.SetTarget(Target(1, path, PlaybackState.Playing));
        session.SetTarget(Target(1, path, PlaybackState.Paused));
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Paused, "pause lands");

        session.SetTarget(Target(1, path, PlaybackState.Playing));
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Playing, "resume lands");
    }

    [Fact]
    public async Task A_trigger_happy_target_burst_is_dispatched_in_order_and_converges()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseLoad.Task : null;
        var a = @"C:\music\a.mp3";
        var b = @"C:\music\b.mp3";

        session.SetTarget(Target(1, a, PlaybackState.Playing));
        await TestWait.Assert(() => engine.Ops.Count != 0, "the first load parks in the engine");
        session.SetTarget(Target(1, a, PlaybackState.Paused));
        session.SetTarget(Target(1, a, PlaybackState.Playing));
        session.SetTarget(Target(1, a, PlaybackState.Paused));
        session.Seek(TimeSpan.FromSeconds(12));
        session.SetTarget(null);
        session.SetTarget(Target(2, b, PlaybackState.Playing));
        releaseLoad.TrySetResult();

        await TestWait.Assert(
            () => engine.Snapshot is { State: PlaybackState.Playing, Path: @"C:\music\b.mp3" },
            "the final target wins");
        Assert.Equal(
            ["Load", "Pause", "Resume", "Pause", "Seek", "Stop", "Load"],
            engine.Ops.Take(7));
    }

    [Fact]
    public async Task Reconnect_only_restores_a_volume_that_was_actually_set()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);

        session.OnEngineReconnected();
        await Task.Delay(150);
        Assert.DoesNotContain("SetVolume", engine.Ops);

        session.SetVolume(0.4f);
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.4f) < 0.001f, "volume lands");
        session.OnEngineReconnected();

        await TestWait.Assert(() => engine.Ops.Count(o => o == "SetVolume") == 2, "volume restored");
        Assert.Equal(0.4f, engine.LastVolume, 3);
    }

    [Fact]
    public async Task Reconnect_lets_the_owner_resolve_policy_before_loading_again()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);
        var a = @"C:\music\a.mp3";
        var b = @"C:\music\b.mp3";
        EngineSessionEnded? ended = null;
        session.OnPlaybackEnded += value => ended = value;
        session.OnReconnected += () =>
            session.SetTarget(Target(2, b, PlaybackState.Playing));

        session.SetTarget(Target(1, a, PlaybackState.Playing));
        await TestWait.Assert(() => engine.Snapshot.Path == a, "the original target loads");
        engine.DisconnectTrack();
        await TestWait.Assert(() => ended?.Reason == EndReason.Disconnected,
            "the disconnect reaches the session");

        session.OnEngineReconnected();

        await TestWait.Assert(() => engine.Snapshot.Path == b, "the newly resolved target loads");
        Assert.Equal(
            [a, b],
            engine.Calls
                .Where(call => call.Op == "Load")
                .Select(call => ((ValueTuple<string, TimeSpan, bool>)call.Arg!).Item1));
    }

    [Fact]
    public async Task A_failed_load_is_reported_and_does_not_kill_the_session()
    {
        var engine = new FakeRemoteEngine();
        await using var session = new EngineSession(engine);
        EngineSessionEnded? ended = null;
        session.OnPlaybackEnded += value => ended = value;
        engine.Intercept = op => op == "Load"
            ? new InvalidOperationException("host rejected the load")
            : null;

        session.SetTarget(Target(7, @"C:\music\bad.mp3", PlaybackState.Playing));
        await TestWait.Assert(() => ended is not null, "the failed target is reported");
        Assert.Equal(new EngineSessionEnded(7, EndReason.Failed), ended);
        engine.Intercept = null;

        session.SetVolume(0.7f);
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.7f) < 0.001f,
            "the session is alive after the failure");
    }
}
