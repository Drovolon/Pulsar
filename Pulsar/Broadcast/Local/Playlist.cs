using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Local;

internal sealed record PlaylistView(
    IReadOnlyList<LocalTrack> Tracks,
    int Index,
    bool Shuffle,
    PlaybackPosition? Position,
    PlaybackState State,
    LocalTrack? CurrentTrack,
    LocalTrack? NextTrack);

internal readonly record struct EndTransition(bool Notify, bool TargetChanged);

/// <summary>
/// State machine for a local playlist. Has no mailbox, events, engine, async, etc.
/// The caller must ensure thread-safe use. Used by LocalSource exclusively.
/// </summary>
internal sealed class Playlist(IReadOnlyList<LocalTrack> tracks)
{
    private LocalTrack[] originalTracks = [.. tracks];
    private LocalTrack[] playlist = [.. tracks];
    private int index;
    private bool shuffle;
    private PlaybackState desiredState = PlaybackState.Stopped;
    private long targetRevision;
    private LocalTrack? observedTrack;
    private int consecutiveFailures;
    private PlaybackPosition? position;
    private PlaybackState observedState;

    internal EngineTarget? Target
    {
        get
        {
            var track = DesiredTrack();
            return track is null
                ? null
                : new EngineTarget(targetRevision, track.FilePath, desiredState, TimeSpan.Zero);
        }
    }

    internal PlaylistView View
    {
        get
        {
            var selected = playlist.Length == 0 ? null : playlist[index];
            var current = observedState == PlaybackState.Stopped
                ? selected
                : observedTrack ?? selected;
            var next = playlist.Length == 0 ? null : playlist[(index + 1) % playlist.Length];
            return new PlaylistView(
                playlist, index, shuffle, position, observedState, current, next);
        }
    }

    internal bool PlayIndex(int value)
    {
        if (value < 0 || value >= playlist.Length) return false;
        index = value;
        desiredState = PlaybackState.Playing;
        consecutiveFailures = 0;
        Reload();
        return true;
    }

    internal bool Play()
    {
        desiredState = playlist.Length == 0
            ? PlaybackState.Stopped
            : PlaybackState.Playing;
        consecutiveFailures = 0;
        Reload();
        return true;
    }

    internal bool Stop()
    {
        desiredState = PlaybackState.Stopped;
        consecutiveFailures = 0;
        return true;
    }

    internal bool Next()
    {
        desiredState = playlist.Length == 0
            ? PlaybackState.Stopped
            : PlaybackState.Playing;
        consecutiveFailures = 0;
        Advance();
        Reload();
        return true;
    }

    internal bool Previous()
    {
        desiredState = playlist.Length == 0
            ? PlaybackState.Stopped
            : PlaybackState.Playing;
        consecutiveFailures = 0;
        if (position is null || position.Current.TotalSeconds <= 5)
        {
            index--;
            if (index < 0) index++;
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

    internal void SetShuffle(bool value)
    {
        if (shuffle == value) return;
        shuffle = value;
        if (playlist.Length == 0) return;
        var current = playlist[index];
        playlist = value ? [.. playlist.Shuffle()] : originalTracks;
        index = Array.FindIndex(playlist, track => SamePath(track, current));
    }

    internal bool Replace(IReadOnlyList<LocalTrack> tracks)
    {
        var current = desiredState != PlaybackState.Stopped && playlist.Length > 0
            ? playlist[index]
            : null;
        originalTracks = [.. tracks];
        playlist = shuffle ? [.. tracks.Shuffle()] : originalTracks;
        var newIndex = current is null
            ? -1
            : Array.FindIndex(playlist, track => SamePath(track, current));
        index = newIndex >= 0 ? newIndex : 0;

        if (current is not null && newIndex < 0)
        {
            desiredState = PlaybackState.Stopped;
            consecutiveFailures = 0;
            return true;
        }
        if (current is not null && newIndex >= 0 && observedTrack is not null)
            observedTrack = playlist[newIndex];
        return false;
    }

    internal bool Observe(EngineObservation observation)
    {
        if (observation.Revision != targetRevision) return false;
        var snapshot = observation.Snapshot;
        if (snapshot.State != PlaybackState.Stopped
            && !string.Equals(
                snapshot.Path, DesiredTrack()?.FilePath,
                StringComparison.OrdinalIgnoreCase))
            return false;
        var nextObservedTrack = snapshot.State == PlaybackState.Stopped
            ? null
            : DesiredTrack();
        var notify = observation.Discrete
            || snapshot.State != observedState
            || !SameTrack(observedTrack, nextObservedTrack);
        observedState = snapshot.State;
        position = snapshot.Position;
        observedTrack = nextObservedTrack;
        return notify;
    }

    internal EndTransition End(EngineSessionEnded ended)
    {
        if (ended.Revision != targetRevision || desiredState == PlaybackState.Stopped)
            return default;
        switch (ended.Reason)
        {
            case EndReason.Disconnected:
                observedState = PlaybackState.Stopped;
                position = null;
                observedTrack = null;
                return new EndTransition(Notify: true, TargetChanged: false);
            case EndReason.Failed:
            {
                consecutiveFailures++;
                if (playlist.Length == 0 || consecutiveFailures >= playlist.Length)
                {
                    Plugin.Log.Warning(
                        $"Local playback: no playable tracks ({consecutiveFailures} consecutive load failures); stopping.");
                    desiredState = PlaybackState.Stopped;
                    consecutiveFailures = 0;
                    observedState = PlaybackState.Stopped;
                    position = null;
                    observedTrack = null;
                    return new EndTransition(Notify: true, TargetChanged: true);
                }

                break;
            }
            case EndReason.Finished:
            default:
                consecutiveFailures = 0;
                break;
        }

        Advance();
        Reload();
        return new EndTransition(Notify: false, TargetChanged: true);
    }

    private void Reload()
    {
        targetRevision++;
        position = null;
    }

    private void Advance()
    {
        if (playlist.Length == 0) return;
        index++;
        if (index >= playlist.Length) index = 0;
    }

    private LocalTrack? DesiredTrack()
        => desiredState == PlaybackState.Stopped || playlist.Length == 0
            ? null
            : playlist[index];

    private static bool SamePath(LocalTrack left, LocalTrack right)
        => string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameTrack(LocalTrack? left, LocalTrack? right)
        => left is null ? right is null : right is not null && SamePath(left, right);
}
