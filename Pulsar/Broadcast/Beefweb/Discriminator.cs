using System;
using System.Diagnostics;
using NAudio.Wave;

namespace Pulsar.Broadcast.Beefweb;

public enum CursorEvent
{
    TrackChange,
    StateChange, // e.g. playing -> paused
    Seek
}

/// <summary>
/// Turns a stream of Observations into discrete cursor events by diffing consecutive samples.
/// Fires ONLY on track change, state change, and (inferred) seek. This is needed because
/// the beefweb API fires events for things like volume change - even if the track hasn't seeked. (sought?)
/// TLDR: we avoid pushing events to sync plugins just because someone adjusted volume.
/// </summary>
public sealed class Discriminator
{
    // Fixed tolerance: this is how far away the cursor can be from our last observation before we consider it
    // a seek event and fire the various on-changed handlers etc.
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(0.5);

    private Observation? last;

    /// <summary>Classify a sample; returns an event if one occurred, else null.</summary>
    public CursorEvent? Classify(Observation o)
    {
        var possiblePrev = last;
        last = o;

        // First sample: if something is playing/paused, broadcast it
        if (possiblePrev is not { } prev)
            return o.State != PlaybackState.Stopped ? CursorEvent.TrackChange : null;

        if (prev.RawPath != o.RawPath) return CursorEvent.TrackChange;
        if (prev.State != o.State) return CursorEvent.StateChange;

        // Seek only if the cursor on the player side has moved more than it should have
        // since our last observation
        var dtMono = Stopwatch.GetElapsedTime(prev.MonoStamp, o.MonoStamp);
        var dPos = o.Position - prev.Position;
        var expected = o.State == PlaybackState.Playing ? dtMono : TimeSpan.Zero;
        return (dPos - expected).Duration() > SeekTolerance ? CursorEvent.Seek : null;
    }
}
