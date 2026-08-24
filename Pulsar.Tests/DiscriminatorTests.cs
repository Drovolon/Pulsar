using System;
using System.Diagnostics;
using NAudio.Wave;
using Pulsar.Broadcast.Beefweb;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Event-classification scenarios for the Discriminator: beefweb pushes an update for
/// every little thing (volume, playlist edits), and only real cursor events - track
/// change, play/pause, seek - may wake the sync pipeline. Observations carry their own
/// monotonic timestamps, so these tests fabricate time instead of sleeping.
/// </summary>
public class DiscriminatorTests
{
    private static readonly long Base = Stopwatch.GetTimestamp();

    /// <summary>An observation as the feed would produce it, `atSeconds` after Base.</summary>
    private static Observation Obs(
        string? path = "/music/a.flac", PlaybackState state = PlaybackState.Playing, double positionSeconds = 10,
        double atSeconds = 0) =>
        new(path, state, TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(180), "", "", DateTimeOffset.UtcNow,
            Base + (long)(atSeconds * Stopwatch.Frequency));

    [Fact]
    public void First_sample_of_a_playing_track_is_a_track_change()
    {
        var d = new Discriminator();
        Assert.Equal(CursorEvent.TrackChange, d.Classify(Obs()));
    }

    [Fact]
    public void First_sample_of_a_stopped_player_is_nothing()
    {
        // Connecting to an idle player must not wake the pipeline.
        var d = new Discriminator();
        Assert.Null(d.Classify(Obs(null, PlaybackState.Stopped, 0)));
    }

    [Fact]
    public void Steady_playback_produces_no_events()
    {
        // A cursor advancing in step with wall time is business as usual (e.g. a volume change).
        var d = new Discriminator();
        d.Classify(Obs(positionSeconds: 10, atSeconds: 0));

        Assert.Null(d.Classify(Obs(positionSeconds: 11, atSeconds: 1)));
        Assert.Null(d.Classify(Obs(positionSeconds: 12, atSeconds: 2)));
    }

    [Fact]
    public void Small_cursor_drift_stays_under_the_seek_tolerance()
    {
        var d = new Discriminator();
        d.Classify(Obs(positionSeconds: 10, atSeconds: 0));

        // 0.3s ahead of ideal: measurement jitter, not a seek.
        Assert.Null(d.Classify(Obs(positionSeconds: 11.3, atSeconds: 1)));
    }

    [Fact]
    public void A_new_path_is_a_track_change()
    {
        var d = new Discriminator();
        d.Classify(Obs("/music/a.flac"));

        Assert.Equal(CursorEvent.TrackChange, d.Classify(Obs("/music/b.flac", positionSeconds: 0, atSeconds: 1)));
    }

    [Fact]
    public void A_new_path_wins_over_a_simultaneous_state_change()
    {
        // Track advanced AND paused in the same sample: reporting only a state change
        // would leave listeners on the old file.
        var d = new Discriminator();
        d.Classify(Obs("/music/a.flac"));

        Assert.Equal(CursorEvent.TrackChange, d.Classify(Obs("/music/b.flac", PlaybackState.Paused, 0, 1)));
    }

    [Fact]
    public void Pausing_is_a_state_change_and_holding_there_is_nothing()
    {
        var d = new Discriminator();
        d.Classify(Obs(positionSeconds: 10, atSeconds: 0));

        Assert.Equal(CursorEvent.StateChange,
                     d.Classify(Obs(state: PlaybackState.Paused, positionSeconds: 10.2, atSeconds: 0.2)));
        // Paused player re-observed later: cursor unmoved, nothing happened.
        Assert.Null(d.Classify(Obs(state: PlaybackState.Paused, positionSeconds: 10.2, atSeconds: 3)));
    }

    [Theory]
    [InlineData(25.0)] // jumped forward
    [InlineData(2.0)]  // jumped backward
    public void A_cursor_jump_while_playing_is_a_seek(double newPosition)
    {
        var d = new Discriminator();
        d.Classify(Obs(positionSeconds: 10, atSeconds: 0));

        Assert.Equal(CursorEvent.Seek, d.Classify(Obs(positionSeconds: newPosition, atSeconds: 1)));
    }

    [Fact]
    public void A_cursor_move_while_paused_is_a_seek()
    {
        // Paused means the cursor should not move at all; any movement is the DJ scrubbing.
        var d = new Discriminator();
        d.Classify(Obs(state: PlaybackState.Paused, positionSeconds: 10, atSeconds: 0));

        Assert.Equal(CursorEvent.Seek, d.Classify(Obs(state: PlaybackState.Paused, positionSeconds: 11, atSeconds: 1)));
    }
}
