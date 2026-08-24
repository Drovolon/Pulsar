using System;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Listening;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// SyncDecider is the pure heart of listener-side sync: given the last applied state
/// (current), the DJ's latest cursor (desired), and engine truth, pick an action.
/// Constants under test: MaxLag = 15s (freshness grace + drift deadzone + outro window),
/// TargetLag = 5s (how far to deliberately trail the DJ).
/// </summary>
public class SyncDeciderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string Song = @"C:\sync\song.opus";
    private const string OtherSong = @"C:\sync\other.opus";

    private static PairData Dj(
        string file = Song, double posSec = 0, bool playing = true, double observedAgoSec = 0, int epoch = 1) =>
        new(file, TimeSpan.FromSeconds(posSec), playing, Now - TimeSpan.FromSeconds(observedAgoSec), epoch, null);

    private static EngineSnapshot Engine(
        string? file = null, double posSec = 0, double totalSec = 180, PlaybackState state = PlaybackState.Stopped) =>
        new(state, file, null,
            file is null ? null : new PlaybackPosition(TimeSpan.FromSeconds(posSec), TimeSpan.FromSeconds(totalSec)), Now);

    // ---- joining / loading ----------------------------------------------------------

    [Fact]
    public void Fresh_song_starts_from_the_beginning_not_mid_intro()
    {
        // The DJ started 8s ago; sync needed a few seconds to deliver the file.
        // We should NOT start 8s in - the intro plays from zero (djPos <= MaxLag).
        var action = SyncDecider.Decide(null, Dj(posSec: 8), Engine());

        var load = Assert.IsType<EngineAction.Load>(action);
        Assert.Equal(TimeSpan.Zero, load.Position);
        Assert.True(load.Playing);
    }

    [Fact]
    public void Joining_mid_song_trails_the_dj_by_target_lag()
    {
        // Walking up to a venue mid-song: start where the DJ is, minus the cushion.
        // Only this test asserts TargetLag's literal value (5s); the others use the
        // constant, so retuning it breaks exactly one test.
        var action = SyncDecider.Decide(null, Dj(posSec: 60), Engine());

        var load = Assert.IsType<EngineAction.Load>(action);
        Assert.Equal(TimeSpan.FromSeconds(55), load.Position);
    }

    [Fact]
    public void Dj_position_is_extrapolated_from_observation_age_while_playing()
    {
        // Cursor said 60s, but that was 10s ago and the DJ is playing: they're at ~70s now.
        var action = SyncDecider.Decide(null, Dj(posSec: 60, observedAgoSec: 10), Engine());

        var load = Assert.IsType<EngineAction.Load>(action);
        Assert.Equal(TimeSpan.FromSeconds(70) - SyncDecider.TargetLag, load.Position);
    }

    [Fact]
    public void Paused_dj_position_is_taken_verbatim_no_extrapolation()
    {
        var action = SyncDecider.Decide(null, Dj(posSec: 60, playing: false, observedAgoSec: 30), Engine());

        var load = Assert.IsType<EngineAction.Load>(action);
        Assert.Equal(TimeSpan.FromSeconds(60) - SyncDecider.TargetLag, load.Position); // NOT extrapolated to 90
        Assert.False(load.Playing);
    }

    // ---- stop / dedup ---------------------------------------------------------------

    [Fact]
    public void Dj_stop_stops_the_engine_only_if_something_is_loaded()
    {
        Assert.IsType<EngineAction.Stop>(SyncDecider.Decide(Dj(), null, Engine(Song, 30, state: PlaybackState.Playing)));
        Assert.IsType<EngineAction.None>(SyncDecider.Decide(null, null, Engine()));
    }

    [Fact]
    public void Same_cursor_epoch_is_a_no_op_regardless_of_engine_drift()
    {
        // The dedup invariant: once an epoch is applied, re-delivery of the same cursor
        // must not fight the engine (even if we've drifted - drift is epoch-local).
        var applied = Dj(posSec: 10, epoch: 7);
        var redelivered = Dj(posSec: 90, epoch: 7);

        var action = SyncDecider.Decide(applied, redelivered, Engine(Song, 500, state: PlaybackState.Playing));

        Assert.IsType<EngineAction.None>(action);
    }

    [Fact]
    public void Equal_epoch_never_suppresses_a_different_file()
    {
        var applied = Dj(epoch: 7);

        Assert.IsType<EngineAction.Load>(SyncDecider.Decide(applied, Dj(OtherSong, epoch: 7),
                                                            Engine(Song, 60, state: PlaybackState.Playing)));
    }

    // ---- track changes --------------------------------------------------------------

    [Fact]
    public void Track_change_near_the_outro_waits_for_the_song_to_finish()
    {
        // DJ moved on, but we're 10s from the end (< MaxLag): let the outro play out.
        var current = Dj(epoch: 1);
        var next = Dj(OtherSong, epoch: 2);
        var engine = Engine(Song, 170, 180, PlaybackState.Playing);

        Assert.IsType<EngineAction.Wait>(SyncDecider.Decide(current, next, engine));
    }

    [Fact]
    public void Track_change_mid_song_loads_the_new_track_immediately()
    {
        var current = Dj(epoch: 1);
        var next = Dj(OtherSong, epoch: 2);
        var engine = Engine(Song, 60, 180, PlaybackState.Playing);

        var load = Assert.IsType<EngineAction.Load>(SyncDecider.Decide(current, next, engine));
        Assert.Equal(OtherSong, load.Path);
    }

    [Fact]
    public void Track_change_does_not_wait_on_a_track_we_never_played()
    {
        // Engine is playing something that isn't the "current" pair state (e.g. a
        // stale load from a previous source) - no outro courtesy for it.
        var current = Dj(epoch: 1);
        var next = Dj(OtherSong, epoch: 2);
        var engine = Engine(@"C:\sync\unrelated.opus", 175, 180, PlaybackState.Playing);

        Assert.IsType<EngineAction.Load>(SyncDecider.Decide(current, next, engine));
    }

    // ---- steady-state drift ---------------------------------------------------------

    [Fact]
    public void Small_drift_is_left_alone_large_drift_is_reseeked()
    {
        var current = Dj(posSec: 100, epoch: 1);
        var desired = Dj(posSec: 100, epoch: 2); // e.g. DJ seeked, new epoch, startPos = 95

        // Engine within MaxLag of startPos: leave it.
        var closeEngine = Engine(Song, 90, state: PlaybackState.Playing);
        Assert.IsType<EngineAction.None>(SyncDecider.Decide(current, desired, closeEngine));

        // Engine hopelessly behind: snap back into alignment.
        var farEngine = Engine(Song, 40, state: PlaybackState.Playing);
        var seek = Assert.IsType<EngineAction.Seek>(SyncDecider.Decide(current, desired, farEngine));
        Assert.Equal(TimeSpan.FromSeconds(100) - SyncDecider.TargetLag, seek.Position);
    }

    // ---- pause / resume -------------------------------------------------------------

    [Fact]
    public void Dj_pause_and_resume_translate_to_engine_pause_and_resume()
    {
        var current = Dj(epoch: 1);

        var pause = SyncDecider.Decide(current, Dj(posSec: 60, playing: false, epoch: 2),
                                       Engine(Song, 55, state: PlaybackState.Playing));
        Assert.IsType<EngineAction.Pause>(pause);

        var resume = SyncDecider.Decide(current, Dj(posSec: 60, epoch: 2), Engine(Song, 55, state: PlaybackState.Paused));
        Assert.IsType<EngineAction.Resume>(resume);
    }

    [Fact]
    public void Dj_seek_while_both_paused_moves_the_engine()
    {
        var current = Dj(posSec: 30, playing: false, epoch: 1);
        var desired = Dj(posSec: 120, playing: false, epoch: 2);

        var seek = Assert.IsType<EngineAction.Seek>(SyncDecider.Decide(
                                                        current, desired, Engine(Song, 30, state: PlaybackState.Paused)));
        Assert.Equal(TimeSpan.FromSeconds(120) - SyncDecider.TargetLag, seek.Position);
    }
}
