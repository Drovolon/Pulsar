using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Local;

public readonly record struct QueueEntryId(long Value);

public sealed record QueueEntry(QueueEntryId Id, LocalTrack Track);

/// <summary>
/// One row shown in Up Next. Manually queued rows have an ID so they can be edited.
/// </summary>
public sealed record UpcomingTrack(LocalTrack Track, QueueEntryId? QueueEntryId)
{
    public bool IsQueued => QueueEntryId is not null;
}

internal sealed record PlaylistView(
    IReadOnlyList<QueueEntry> Queue,
    IReadOnlyList<UpcomingTrack> Upcoming,
    PlaybackPosition? Position,
    PlaybackState State,
    LocalTrack? CurrentTrack,
    LocalTrack? NextTrack);

internal readonly record struct EndTransition(bool Notify, bool TargetChanged, bool QueueChanged = false);

internal readonly record struct ObserveTransition(bool Accepted, bool CursorChanged);

/// <summary>
/// Playback-order state machine. The active source is the normal repeating
/// traversal; the manual queue is a consumable FIFO override in front of it.
/// This type has no mailbox, events, engine, or async behavior. LocalSource is
/// its sole caller and provides serialization.
/// </summary>
internal sealed class Playlist
{
    private LocalTrack[] source = [];
    private QueueEntry[] queue = [];
    private int sourceIndex;
    private QueueEntry? currentOverride;
    private long nextEntryId;
    private PlaybackState desiredState = PlaybackState.Stopped;
    private long targetRevision;
    private LocalTrack? observedTrack;
    private int consecutiveFailures;
    private int failureLimit;
    private PlaybackPosition? position;
    private PlaybackState observedState;
    private long observedRevision = -1;
    private DateTimeOffset? observedAt;

    internal EngineTarget? Target
    {
        get
        {
            var track = DesiredTrack();
            return track is null ? null : new EngineTarget(targetRevision, track.FilePath, desiredState, TimeSpan.Zero);
        }
    }

    internal bool HasActiveSource => source.Length > 0;
    internal bool TargetIsConfirmed => observedRevision == targetRevision;

    internal PlaylistView View
    {
        get
        {
            var selected = SelectedTrack();
            var current = observedState == PlaybackState.Stopped ? selected : observedTrack ?? selected;
            var upcoming = BuildUpcoming();
            return new PlaylistView(queue, upcoming, position, observedState, current,
                                    upcoming.Count == 0 ? null : upcoming[0].Track);
        }
    }

    internal bool PlaySource(IReadOnlyList<LocalTrack> tracks, LocalTrack track)
    {
        source = [.. tracks];
        sourceIndex = Array.FindIndex(source, candidate => SameTrack(candidate, track));
        if (sourceIndex < 0)
        {
            source = [track];
            sourceIndex = 0;
        }

        currentOverride = null;
        desiredState = PlaybackState.Playing;
        ResetFailures();
        Reload();
        return true;
    }

    internal bool PlaySourceTrack(LocalTrack track)
    {
        var selected = Array.FindIndex(source, candidate => SameTrack(candidate, track));
        if (selected < 0) return false;
        sourceIndex = selected;
        currentOverride = null;
        desiredState = PlaybackState.Playing;
        ResetFailures();
        Reload();
        return true;
    }

    /// <summary>
    /// Replaces the repeating source without reloading the current target. A current
    /// track outside the replacement source remains selected until it ends, with a
    /// source index of -1 meaning traversal resumes at the replacement's first track.
    /// </summary>
    internal void SetSource(IReadOnlyList<LocalTrack> tracks)
    {
        var current = desiredState == PlaybackState.Stopped ? null : SelectedTrack();
        source = [.. tracks];
        ResetFailures();

        if (current is null)
        {
            currentOverride = null;
            sourceIndex = source.Length == 0 ? -1 : 0;
            return;
        }

        sourceIndex = Array.FindIndex(source, candidate => SameTrack(candidate, current));
        currentOverride = sourceIndex >= 0 ? null : new QueueEntry(default, current);
    }

    internal bool Play(QueueEntryId id)
    {
        var selected = Array.FindIndex(queue, entry => entry.Id == id);
        if (selected < 0) return false;
        currentOverride = queue[selected];
        queue = queue.Where((_, index) => index != selected).ToArray();
        desiredState = PlaybackState.Playing;
        ResetFailures();
        Reload();
        return true;
    }

    internal void AddNext(LocalTrack track) => queue = [NewEntry(track), .. queue];

    internal void AddToEnd(LocalTrack track) => queue = [.. queue, NewEntry(track)];

    internal void Remove(QueueEntryId id) => queue = queue.Where(entry => entry.Id != id).ToArray();

    internal void ClearQueue() => queue = [];

    internal void Move(QueueEntryId id, int offset)
    {
        var from = Array.FindIndex(queue, entry => entry.Id == id);
        var to = from + offset;
        if (from < 0 || to < 0 || to >= queue.Length || from == to) return;
        var reordered = queue.ToList();
        var item = reordered[from];
        reordered.RemoveAt(from);
        reordered.Insert(to, item);
        queue = [.. reordered];
    }

    internal void ShuffleUpcoming()
    {
        if (source.Length < 2) return;

        if (sourceIndex < 0)
        {
            source = [.. source.Shuffle()];
            return;
        }

        // Keep the current source track first and leave the manual queue unchanged.
        var current = source[sourceIndex];
        var upcoming = Enumerable.Range(1, source.Length - 1)
                                 .Select(offset => source[(sourceIndex + offset) % source.Length])
                                 .Shuffle();
        source = [current, .. upcoming];
        sourceIndex = 0;
    }

    internal bool Play()
    {
        if (SelectedTrack() is null)
            if (!TryConsumeQueue())
                return false;

        desiredState = PlaybackState.Playing;
        ResetFailures();
        Reload();
        return true;
    }

    internal bool Stop()
    {
        desiredState = PlaybackState.Stopped;
        ResetFailures();
        return true;
    }

    internal bool Next()
    {
        if (SelectedTrack() is null && queue.Length == 0) return false;
        desiredState = PlaybackState.Playing;
        ResetFailures();
        SelectNext();
        if (SelectedTrack() is null)
        {
            desiredState = PlaybackState.Stopped;
            return true;
        }

        Reload();
        return true;
    }

    internal bool Previous()
    {
        if (SelectedTrack() is null) return false;
        desiredState = PlaybackState.Playing;
        ResetFailures();
        if (position is null || position.Current.TotalSeconds <= 5)
        {
            if (currentOverride is not null)
            {
                currentOverride = null;
                if (sourceIndex < 0 && source.Length > 0)
                    sourceIndex = 0;
            }
            else if (sourceIndex > 0)
                sourceIndex--;
        }

        Reload();
        return true;
    }

    internal bool Pause()
    {
        if (desiredState == PlaybackState.Stopped) return false;
        desiredState = PlaybackState.Paused;
        return true;
    }

    internal bool Resume()
    {
        if (desiredState == PlaybackState.Stopped) return false;
        desiredState = PlaybackState.Playing;
        return true;
    }

    internal ObserveTransition Observe(EngineObservation observation)
    {
        if (observation.Revision != targetRevision) return default;
        var snapshot = observation.Snapshot;
        if (snapshot.State != PlaybackState.Stopped &&
            !string.Equals(snapshot.Path, DesiredTrack()?.FilePath, StringComparison.OrdinalIgnoreCase))
            return default;
        var nextObservedTrack = snapshot.State == PlaybackState.Stopped ? null : DesiredTrack();
        var missedSeek = !observation.Discrete &&
                         observedAt is { } previousAsOf &&
                         position is { } previousPosition &&
                         snapshot.Position is { } currentPosition &&
                         snapshot.State == observedState &&
                         observedState != PlaybackState.Stopped &&
                         SameOptionalTrack(observedTrack, nextObservedTrack) &&
                         (currentPosition.Current -
                          previousPosition.Current -
                          (snapshot.State == PlaybackState.Playing ? snapshot.ObservedAt - previousAsOf : TimeSpan.Zero))
                         .Duration() >
                         TimeSpan.FromSeconds(2);
        var notify = observation.Discrete ||
                     missedSeek ||
                     snapshot.State != observedState ||
                     !SameOptionalTrack(observedTrack, nextObservedTrack);
        observedState = snapshot.State;
        position = snapshot.Position;
        observedTrack = nextObservedTrack;
        observedRevision = observation.Revision;
        observedAt = snapshot.ObservedAt;
        return new ObserveTransition(true, notify);
    }

    internal EndTransition End(EngineSessionEnded ended)
    {
        if (ended.Revision != targetRevision || desiredState == PlaybackState.Stopped)
            return default;
        if (ended.Reason == EndReason.Disconnected)
        {
            observedState = PlaybackState.Stopped;
            position = null;
            observedTrack = null;
            return new EndTransition(true, false);
        }

        if (ended.Reason == EndReason.Failed)
        {
            if (consecutiveFailures == 0) failureLimit = FailureCycleLength();
            consecutiveFailures++;
            if (failureLimit == 0 || consecutiveFailures >= failureLimit)
            {
                Plugin.Log.Warning(
                    $"Local playback: no playable tracks ({consecutiveFailures} consecutive load failures); stopping.");
                desiredState = PlaybackState.Stopped;
                ResetFailures();
                observedState = PlaybackState.Stopped;
                position = null;
                observedTrack = null;
                return new EndTransition(true, true);
            }
        }
        else
            ResetFailures();

        var queueCount = queue.Length;
        SelectNext();
        if (SelectedTrack() is null)
        {
            desiredState = PlaybackState.Stopped;
            observedState = PlaybackState.Stopped;
            observedTrack = null;
            position = null;
            return new EndTransition(true, true, queue.Length != queueCount);
        }

        Reload();
        return new EndTransition(false, true, queue.Length != queueCount);
    }

    private IReadOnlyList<UpcomingTrack> BuildUpcoming()
    {
        var sourceCount = sourceIndex < 0 ? source.Length : Math.Max(0, source.Length - 1);
        var result = new List<UpcomingTrack>(queue.Length + sourceCount);
        result.AddRange(queue.Select(entry => new UpcomingTrack(entry.Track, entry.Id)));
        for (var offset = 1; offset <= sourceCount; offset++)
            result.Add(new UpcomingTrack(source[(sourceIndex + offset) % source.Length], null));
        return result;
    }

    private void SelectNext()
    {
        if (TryConsumeQueue()) return;
        currentOverride = null;
        if (source.Length == 0) return;
        sourceIndex++;
        if (sourceIndex >= source.Length) sourceIndex = 0;
    }

    private bool TryConsumeQueue()
    {
        if (queue.Length == 0) return false;
        currentOverride = queue[0];
        queue = queue[1..];
        return true;
    }

    private int FailureCycleLength() => queue.Length + source.Length + (currentOverride is null ? 0 : 1);

    private void ResetFailures()
    {
        consecutiveFailures = 0;
        failureLimit = 0;
    }

    private void Reload()
    {
        targetRevision++;
        position = null;
    }

    private LocalTrack? DesiredTrack() => desiredState == PlaybackState.Stopped ? null : SelectedTrack();

    private LocalTrack? SelectedTrack() => currentOverride?.Track ?? (source.Length == 0 ? null : source[sourceIndex]);

    private QueueEntry NewEntry(LocalTrack track) => new(new QueueEntryId(++nextEntryId), track);

    private static bool SameTrack(LocalTrack left, LocalTrack right) =>
        string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameOptionalTrack(LocalTrack? left, LocalTrack? right) =>
        left is null ? right is null : right is not null && SameTrack(left, right);
}
