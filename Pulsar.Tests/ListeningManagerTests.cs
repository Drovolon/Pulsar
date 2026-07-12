using System;
using System.Linq;
using System.Threading.Tasks;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Scenario tests for the listener-side coordinator: who plays, when the engine is
/// told to stop, and how volume composes. Reconciliation is asynchronous (signal-driven
/// loop), so tests poll for convergence via TestWait.
/// </summary>
public class ListeningManagerTests : IAsyncLifetime
{
    private const string AliceTrack = @"C:\sync\alice.opus";
    private const string BobTrack = @"C:\sync\bob.opus";

    private readonly FakeRemoteEngine engine = new();
    private ListeningManager? manager;

    private ListeningManager Create(float masterVolume = 1f, bool autoPlay = true)
        => manager = new ListeningManager(engine, masterVolume, null, autoPlay);

    private ListeningManager CreateFast(TimeSpan? lostBackoff = null)
        => manager = new ListeningManager(engine, 1f, null, true,
            updatePoll: TimeSpan.FromMilliseconds(25),
            errorBackoff: TimeSpan.FromMilliseconds(50),
            lostBackoff: lostBackoff ?? TimeSpan.FromMilliseconds(50));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (manager is not null) await manager.DisposeAsync();
    }

    private static PairData Playing(string file, int epoch = 1, double rgDb = 0)
        => new(file, TimeSpan.FromSeconds(60), true, DateTimeOffset.UtcNow, epoch,
               rgDb == 0 ? null : new TrackMeta { ReplayGainDb = rgDb });

    private int LoadCount => engine.Ops.Count(o => o == "Load");

    // ---- liveness --------------------------------------------------------------------

    [Fact]
    public async Task Mutators_never_block_even_while_the_reconcile_loop_is_parked_in_a_wedged_rpc()
    {
        // A wedged-but-connected host: the RPC never answers, so the reconcile loop
        // parks inside it holding its internal state. Pair updates arrive on the game's
        // framework and UI threads - blocking them would freeze the whole game.
        var wedge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "SetVolume" ? wedge.Task : null;

        var lm = Create();
        try
        {
            lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
            Assert.True(await engine.WaitForCall("SetVolume"), "the reconcile loop parks in the wedged RPC");

            var mutations = Task.Run(() =>
            {
                lm.AddOrUpdatePair(2, "Bob", Playing(BobTrack));
                lm.SetPairVolume(1, 0.5f);
                lm.ClearPair(2);
            });
            await TestWait.Within(mutations, "mutators while the loop is parked");
        }
        finally
        {
            wedge.TrySetResult(); // always unwedge, or teardown hangs with the test
        }

        // Once the host recovers, the queued mutations apply in order.
        await TestWait.Assert(
            () => lm.View is [{ Ident: 1, Volume: 0.5f }],
            "queued mutations land after the host recovers");
    }

    // ---- source selection -----------------------------------------------------------

    [Fact]
    public async Task Nearby_broadcaster_autoplays()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));

        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "engine loads Alice's track");
        await TestWait.Assert(() => lm.View.Any(p => p is { Active: true, DisplayName: "Alice" }),
            "view marks Alice active");
    }

    [Fact]
    public async Task Autoplay_pick_is_stable_when_others_arrive()
    {
        var lm = Create();
        lm.AddOrUpdatePair(5, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice starts");

        // A lower ident arriving must NOT steal playback (no flapping).
        lm.AddOrUpdatePair(3, "Bob", Playing(BobTrack));
        await Task.Delay(200);
        Assert.Equal(AliceTrack, engine.Snapshot.Path);
    }

    [Fact]
    public async Task Clearing_the_active_pair_stops_the_engine_and_advances_to_the_next()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice starts");
        lm.AddOrUpdatePair(2, "Bob", Playing(BobTrack));

        // Alice walks away / stops broadcasting: her pair is cleared.
        lm.ClearPair(1);

        await TestWait.Assert(() => engine.Snapshot.Path == BobTrack, "Bob takes over after Alice leaves");
        await TestWait.Assert(() => lm.View.Any(p => p is { Active: true, DisplayName: "Bob" }),
            "view marks Bob active");
    }

    [Fact]
    public async Task Clearing_the_only_pair_stops_playback()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice starts");

        lm.ClearPair(1);

        await TestWait.Assert(() => engine.Snapshot.Path is null, "engine stopped after the only DJ left");
    }

    [Fact]
    public async Task Turning_autoplay_off_silences_the_current_source()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice starts");

        // The UI's "Off" card: silence, ignore nearby broadcasters.
        lm.SetAutoPlay(false);

        await TestWait.Assert(() => engine.Snapshot.Path is null, "engine stopped after autoplay off");
    }

    [Fact]
    public async Task Broadcasting_suppresses_playback_until_it_ends()
    {
        var lm = Create();
        lm.SetBroadcasting(true);
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));

        await Task.Delay(200);
        Assert.Null(engine.Snapshot.Path); // no tuning in while we're the DJ

        lm.SetBroadcasting(false);
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice plays once broadcast ends");
    }

    [Fact]
    public async Task A_pinned_source_plays_even_while_broadcasting()
    {
        // The debug-loopback flow: pinning our own loopback pair must play it.
        var lm = Create();
        lm.SetBroadcasting(true);
        lm.AddOrUpdatePair(1, "Debug Loopback", Playing(AliceTrack));
        lm.SetActive(1);

        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "pinned source plays while broadcasting");
    }

    [Fact]
    public async Task Unpinning_falls_back_to_the_ambient_setting()
    {
        var lm = Create(autoPlay: false);
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        lm.SetActive(1);
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "pinned Alice plays");

        lm.Unpin();

        await TestWait.Assert(() => engine.Snapshot.Path is null, "autoplay is off, so unpin means silence");
    }

    // ---- failure handling -----------------------------------------------------------

    [Fact]
    public async Task Failed_loads_are_retried_a_bounded_number_of_times()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => LoadCount == 1, "initial load");

        // The track fails over and over (corrupt file, dead decoder...).
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            engine.FailTrack();
            var expected = attempt + 1;
            await TestWait.Assert(() => LoadCount == expected, $"retry #{attempt}");
        }

        // Fourth failure exhausts the retry budget: no fifth load.
        engine.FailTrack();
        await Task.Delay(300);
        Assert.Equal(4, LoadCount);
    }

    [Fact]
    public async Task A_fresh_cursor_resets_the_retry_budget()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => LoadCount == 1, "initial load");

        // Exhaust the budget, anchoring each failure on the reload it provokes - a blind
        // fail could fire while nothing is loaded, which the real host cannot produce.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            engine.FailTrack();
            var expected = attempt + 1;
            await TestWait.Assert(() => LoadCount == expected, $"retry #{attempt}");
        }
        engine.FailTrack(); // budget gone
        await Task.Delay(300);
        var exhausted = LoadCount;
        Assert.Equal(4, exhausted);

        // DJ moves to the next song: retries start over for the new cursor.
        lm.AddOrUpdatePair(1, "Alice", Playing(BobTrack, epoch: 2));
        await TestWait.Assert(() => LoadCount == exhausted + 1, "new track loads after budget reset");
        Assert.Equal(BobTrack, engine.Snapshot.Path);
    }

    // ---- volume ---------------------------------------------------------------------

    [Fact]
    public async Task Volume_stacks_master_pair_and_replaygain()
    {
        var lm = Create(masterVolume: 0.5f);
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack, rgDb: -6));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "Alice starts");

        lm.SetPairVolume(1, 0.5f);

        var expected = 0.5f * 0.5f * TestData.DbToLinear(-6);
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - expected) < 0.001f,
            $"volume converges to master*pair*rg = {expected}");
    }

    [Fact]
    public async Task Extreme_replaygain_from_a_peer_is_clamped()
    {
        // A peer could send ReplayGainDb = +40: the gain stage must clamp to +-20 dB.
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack, rgDb: 40));

        var clamped = TestData.DbToLinear(20); // 10x, not 100x
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - clamped) < 0.01f,
            "gain clamped to +20 dB");
    }

    [Fact]
    public async Task Mute_silences_and_unmute_restores()
    {
        var lm = Create();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.LastVolume == 1f, "initial volume applied");

        lm.SetPairMuted(1, true);
        await TestWait.Assert(() => engine.LastVolume == 0f, "muted");

        lm.SetPairMuted(1, false);
        await TestWait.Assert(() => engine.LastVolume == 1f, "restored");
    }

    [Fact]
    public async Task Reconnect_reapplies_the_current_volume()
    {
        // A restarted host resets to volume=1: the real value must be re-pushed even
        // though nothing changed plugin-side.
        var lm = Create(masterVolume: 0.3f);
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => Math.Abs(engine.LastVolume - 0.3f) < 0.001f, "initial volume");
        var volumeSends = engine.Ops.Count(o => o == "SetVolume");

        lm.OnEngineReconnected();

        await TestWait.Assert(() => engine.Ops.Count(o => o == "SetVolume") > volumeSends,
            "volume re-sent after reconnect");
        Assert.Equal(0.3f, engine.LastVolume, 3);
    }

    // ---- update loop (UI position surface) --------------------------------------

    [Fact]
    public async Task The_update_loop_surfaces_position_and_playing_state()
    {
        var lm = CreateFast();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));

        await TestWait.Assert(() => lm.ActiveNowPlaying, "playing state surfaces");
        await TestWait.Assert(() => lm.ActivePosition is { Total.TotalMinutes: 3 },
            "position (with the fake's 3min duration) surfaces");
    }

    [Fact]
    public async Task The_update_loop_recovers_after_a_connection_loss()
    {
        var lm = CreateFast();
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => lm.ActiveNowPlaying, "playing before the outage");

        engine.Intercept = op => op == "GetState"
            ? new StreamJsonRpc.ConnectionLostException("host down") : null;
        await Task.Delay(150); // let the loop hit the outage and enter (tiny) backoff
        engine.Intercept = null;

        // Only the poll loop can observe a duration change (no event fires for it).
        engine.TrackDuration = TimeSpan.FromMinutes(5);
        await TestWait.Assert(() => lm.ActivePosition is { Total.TotalMinutes: 5 },
            "poll loop resumed after the connection loss");
    }

    [Fact]
    public async Task Dispose_is_prompt_even_mid_connection_loss_backoff()
    {
        // Production-scale backoff: dispose must cancel the wait, not sit it out.
        var lm = CreateFast(lostBackoff: TimeSpan.FromSeconds(30));
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => lm.ActiveNowPlaying, "playing before the outage");

        engine.Intercept = op => op == "GetState"
            ? new StreamJsonRpc.ConnectionLostException("host down") : null;
        await Task.Delay(150); // loop is now parked in the 30s backoff

        await TestWait.Within(lm.DisposeAsync().AsTask(), "dispose cancels the backoff");
        manager = null;
    }

    // ---- master volume ------------------------------------------------------------

    [Fact]
    public async Task Master_volume_and_mute_compose_into_the_engine_volume()
    {
        var lm = Create(); // master 1, pair 1, no RG => exact values
        lm.AddOrUpdatePair(1, "Alice", Playing(AliceTrack));
        await TestWait.Assert(() => engine.LastVolume == 1f, "initial volume lands");

        lm.SetMasterVolume(0.5f);
        await TestWait.Assert(() => engine.LastVolume == 0.5f, "master volume scales");

        lm.SetMasterMuted(true);
        await TestWait.Assert(() => engine.LastVolume == 0f, "master mute silences");

        lm.SetMasterMuted(false);
        await TestWait.Assert(() => engine.LastVolume == 0.5f, "unmute restores");
    }

    // ---- autoplay tie -------------------------------------------------------------

    [Fact]
    public async Task A_genuine_autoplay_tie_goes_to_the_lowest_ident()
    {
        // Register BOTH pairs while broadcasting blocks resolution, so the stability
        // rule ("keep the current pick") can't mask the tie-break.
        var lm = Create();
        lm.SetBroadcasting(true);
        lm.AddOrUpdatePair(7, "Alice", Playing(AliceTrack));
        lm.AddOrUpdatePair(3, "Bob", Playing(BobTrack));
        await TestWait.Assert(() => lm.View.Count == 2, "both pairs registered");
        Assert.Null(engine.Snapshot.Path); // broadcasting: nothing plays yet

        lm.SetBroadcasting(false); // fresh pick from a real two-way tie

        await TestWait.Assert(() => engine.Snapshot.Path == BobTrack, "lowest ident (3) wins");
    }
}
