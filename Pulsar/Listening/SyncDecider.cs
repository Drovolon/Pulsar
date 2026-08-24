using System;
using NAudio.Wave;
using Pulsar.Common.Api;

namespace Pulsar.Listening;

/// <summary>
/// These are the output of SyncDecider.Decide: what should be done
/// to the engine.
/// </summary>
internal abstract record EngineAction
{
    /// <summary>
    /// Do nothing, except set current = desired. (No engine change needed)
    /// </summary>
    internal sealed record None : EngineAction;

    /// <summary>
    /// Do nothing, but keep current != desired for later reevaluation.
    /// (In other words, a future Reevaluate call will result in a different
    /// EngineAction result.)
    /// </summary>
    internal sealed record Wait : EngineAction;

    /// <summary> Load a new song! if Playing, then also start playing it. </summary>
    internal sealed record Load(string Path, TimeSpan Position, bool Playing) : EngineAction;

    internal sealed record Seek(TimeSpan Position) : EngineAction;

    internal sealed record Pause : EngineAction;

    internal sealed record Resume : EngineAction;

    internal sealed record Stop : EngineAction;
}

/// <summary>
/// This complicated little bugger is the heart of how we decide what to actually do
/// with the audio output, compared to what data we receive. We use a current/desired
/// model: what was the "last applied state" and what is the "desired state".
/// It's the job of SyncDecider to figure out what to do the playback engine to make
/// current match desired.
///
/// Also, this is all pure code (stateless) to enable unit testing.
/// </summary>
internal static class SyncDecider
{
    /// <summary>
    /// How far behind the DJ you're allowed to be before you're forcibly seeked (sought?)
    /// back into alignment with the DJ. This mostly exists to make song intros and outros
    /// not clipped. Intro, because we will set pos=0 (beginning) if djPos lte MaxLag.
    /// Outro, because we will let a song finish playing if remainingTimeLeft lte MaxLag
    /// when the next song comes in.
    /// </summary>
    internal static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(15); // internal for tests

    /// <summary>
    /// How far to target behind the DJ. This adds a little extra cushion onto MaxLag
    /// to allow the DJ to sync their song while we finish it up.
    /// </summary>
    internal static readonly TimeSpan TargetLag = TimeSpan.FromSeconds(5); // internal for tests

    internal static EngineAction Decide(PairData? current, PairData? desired, EngineSnapshot engine)
    {
        var now = engine.ObservedAt;

        // If desired is null, then we should stop - if anything is playing.
        if (desired is null)
            return engine.Path is null ? new EngineAction.None() : new EngineAction.Stop();

        // If we're already on the same cursor epoch, there's nothing to do
        if (current is not null &&
            desired.CursorEpoch == current.CursorEpoch &&
            desired.FilePath == current.FilePath)
            return new EngineAction.None();

        // If desired is to play, calculate the position the DJ is at based on their observedAt timestamp
        var djPos = desired.IsPlaying ? desired.Position + (now - desired.ObservedAt) : desired.Position;
        if (djPos < TimeSpan.Zero) djPos = TimeSpan.Zero;

        // Two things:
        //
        // 1. If the calculated position is below MaxLag, that means the DJ just started playing the song.
        //    In that case, we should start from the beginning, not e.g. 5 seconds into playback.
        //    (Assuming it took 5 seconds for the sync to relay data+file to our client.)
        //    This has two effects: (1) if you arrive at a venue while a song is mid-play, your starting pos
        //    will be mid-song, just like everyone else. (2) BUT, for a freshly started song, we give the
        //    sync a little time to actually sync songs.
        //
        // 2. Otherwise, we follow TargetLag seconds behind to give sync a bit of time to catch up before
        //    the song changes.
        var startPos = djPos <= MaxLag ? TimeSpan.Zero : djPos - TargetLag;

        if (engine.Path != desired.FilePath) // not currently playing the desired song!
        {
            if (current is not null &&
                engine.Path == current.FilePath &&
                engine is { State: PlaybackState.Playing, Position: { } prev })
            {
                // We will wait up to MaxLag seconds for the current song to finish
                // before going to the next one.
                var remaining = prev.Total - prev.Current;
                if (remaining > TimeSpan.Zero && remaining <= MaxLag)
                    return new EngineAction.Wait();
            }

            return new EngineAction.Load(desired.FilePath, startPos, desired.IsPlaying);
        }

        // Engine has the correct file path loaded, so we just need to make it match desired.
        return (isPlaying: desired.IsPlaying, nowPlaying: engine.State == PlaybackState.Playing) switch
        {
            (true, true) => InSync(engine, startPos)
                                ? new EngineAction.None() // already in sync, do nothing
                                : new EngineAction.Seek(startPos),
            (false, true) => new EngineAction.Pause(),
            (true, false) => new EngineAction.Resume(),
            (false, false) => new EngineAction.Seek(startPos),
        };
    }

    private static bool InSync(EngineSnapshot engine, TimeSpan startPos) =>
        engine.Position is { } p && (p.Current - startPos).Duration() <= MaxLag;
}
