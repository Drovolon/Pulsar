using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Playback;
using StreamJsonRpc;

namespace Pulsar.Listening;

public record PairData(
    string FilePath,
    TimeSpan Position,
    bool IsPlaying,
    DateTimeOffset ObservedAt,
    long CursorEpoch,
    TrackMeta? Meta);

internal class PairState(PairData data)
{
    internal PairData Data = data;
    internal string? DisplayName; // resolved character name; null if the address couldn't be resolved
    internal float Volume = 1f;
    internal bool Muted;
    internal int FailCount; // consecutive load failures for Data
}

/// <summary>
/// Flattened immutable view for UI code to look at.
/// </summary>
public readonly record struct PairView(
    ulong Ident,
    string? DisplayName,
    TrackMeta? Meta,
    string? FilePath,
    float Volume,
    bool Muted,
    bool Active, // this source is currently playing audio
    bool Pinned) // this source was selected as an override by the user
{
    public string NameOrFallback => DisplayName ?? $"{Ident:X}";
}

public enum ListenerPlaybackStatus
{
    Idle,
    Loading,
    Playing,
    Paused,
    Ended,
    Failed,
}

/// <summary>Atomic UI-facing state of the one listening playback engine.</summary>
public sealed record ListenerPlaybackView(ListenerPlaybackStatus Status, string? TargetPath, PlaybackPosition? Position);

/// <summary>
/// ListeningManager coordinates listening to songs streamed from other players, via IPC.
/// It drives a single playback engine, fed by at most one "active" source at a time.
///
/// ResolveActive() picks WHICH
/// source should be actually playing audio, right now, by looking at whom the user has pinned
/// (if anyone), whether the user is broadcasting, and whether the user has autoplay enabled.
///
/// On the flip side, SyncDecider.Decide() picks HOW to make the engine match that source's
/// desired state. Like, whether to load a new song, seek in the current song, stop playing, etc.
///
/// Uses the 'actor' model. The processing loop owns all mutable state, and handles
/// source selection and engine state after message processing.
/// </summary>
public class ListeningManager : IAsyncDisposable
{
    private const int MaxLoadRetries = 3;

    private abstract record Message;

    private sealed record PairUpdated(ulong Ident, string? DisplayName, PairData Data) : Message;

    private sealed record PairCleared(ulong Ident) : Message;

    private sealed record ActiveSet(ulong Ident) : Message;

    private sealed record Unpinned : Message;

    private sealed record AutoPlaySet(bool Value) : Message;

    private sealed record BroadcastingSet(bool Value) : Message;

    private sealed record MasterVolumeSet(float Value) : Message;

    private sealed record MasterMutedSet(bool Value) : Message;

    private sealed record PairVolumeSet(ulong Ident, float Value) : Message;

    private sealed record PairMutedSet(ulong Ident, bool Value) : Message;

    private sealed record PlaybackEndedMessage(EngineSessionEnded Ended) : Message;

    private sealed record EngineObserved(EngineObservation Observation) : Message;

    private sealed record EngineReconnected : Message;

    private sealed record PlaybackTarget(ulong SourceId, long CursorEpoch, string Path);

    private sealed record PublishedState(IReadOnlyList<PairView> View, bool AutoPlay, bool HasPin);

    private readonly Dictionary<ulong, PairState> pairs = [];

    private readonly EngineSession engineSession;

    // This is the manager's synchronization boundary: callers and engine callbacks only post
    // messages; the single reader below is the sole owner of all non-published state.
    private readonly Channel<Message> mailbox = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true });

    private readonly Channel<ListeningOutput> outputs =
        Channel.CreateUnbounded<ListeningOutput>(new UnboundedChannelOptions { SingleReader = true });

    internal ChannelReader<ListeningOutput> Outputs => outputs.Reader;

    private float masterVolume;
    private bool masterMuted;

    // True when broadcasting. While broadcasting, we never tune into a nearby broadcaster.
    private bool broadcasting;

    // Pinned character name. This is the user selecting "I want to listen to John Finalfantasy".
    // Note, pins aren't saved in config, so they won't survive a plugin reload etc.
    private string? pinnedName;

    // The stable ambient selection. Pins temporarily override it without destroying it.
    private ulong? autoplaySourceId;
    private ulong? selectedSourceId;

    // Populated during the actor loop, then after reconciliation, drained to push notifications
    private readonly HashSet<ulong> newPairNotifications = [];
    private readonly HashSet<ulong> playbackStartedNotifications = [];

    // Per-source volumes, a map of character name -> volume. Persisted in config by the UI code.
    // Then the UI code calls into this class to push down updates.
    private readonly Dictionary<string, float> preferredVolumes;

    private volatile PublishedState published;

    /// <summary>View for UI code, so it doesn't need to take locks.</summary>
    public IReadOnlyList<PairView> View => published.View;

    private ListenerPlaybackView playback = new(ListenerPlaybackStatus.Idle, null, null);
    private bool lastListening;
    public ListenerPlaybackView Playback => Volatile.Read(ref playback);

    // Kept as convenience views for callers that only need the old two facts.
    public PlaybackPosition? ActivePosition => Playback.Position;
    public bool ActiveNowPlaying => Playback.Status == ListenerPlaybackStatus.Playing;

    /// <summary>
    /// Whether to play nearby broadcasters automatically. Off means silence, and stops
    /// autoplay. A pin overrides all this.
    /// </summary>
    public bool AutoPlay => published.AutoPlay;

    private bool autoPlay;

    /// <summary>Whether anyone is pinned.</summary>
    public bool HasPin => published.HasPin;

    private readonly Task messageLoop;
    private readonly CancellationTokenSource asyncCts = new();

    public ListeningManager(IRemoteEngine engine, Configuration config) : this(
        engine, config.ListeningMasterVolume, config.ListeningPairVolumes, config.ListeningAutoPlay,
        TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)) { }

    // for unit tests only
    internal ListeningManager(
        IRemoteEngine engine, float masterVolume, IReadOnlyDictionary<string, float>? savedPairVolumes, bool autoPlay,
        TimeSpan updatePoll, TimeSpan errorBackoff, TimeSpan lostBackoff)
    {
        engineSession = new EngineSession(
            engine, updatePoll, updatePoll, errorBackoff, TimeSpan.FromSeconds(2), lostBackoff);
        this.masterVolume = masterVolume;
        preferredVolumes = savedPairVolumes is null ? [] : new Dictionary<string, float>(savedPairVolumes);
        this.autoPlay = autoPlay;
        published = new PublishedState([], autoPlay, false);
        engineSession.OnPlaybackEnded += OnPlaybackEnded;
        engineSession.OnObserved += OnEngineObserved;
        engineSession.OnReconnected += OnSessionReconnected;

        messageLoop = MessageLoop(asyncCts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        engineSession.OnPlaybackEnded -= OnPlaybackEnded;
        engineSession.OnObserved -= OnEngineObserved;
        engineSession.OnReconnected -= OnSessionReconnected;
        asyncCts.Cancel();
        mailbox.Writer.TryComplete();
        await messageLoop;
        await engineSession.DisposeAsync();
        asyncCts.Dispose();
        outputs.Writer.TryComplete();
    }

    /// <summary>
    /// Called after the audio host reconnects.
    /// </summary>
    public void OnEngineReconnected() => engineSession.OnEngineReconnected();

    public void AddOrUpdatePair(ulong ident, string? displayName, PairData data) =>
        Post(new PairUpdated(ident, displayName, data));

    public void ClearPair(ulong ident) => Post(new PairCleared(ident));

    /// <summary>Pin a specific source. Ignored if the address can't be resolved to a character name.</summary>
    public void SetActive(ulong ident) => Post(new ActiveSet(ident));

    /// <summary>Unpin makes us fall back to either autoplay or off (depending on what's configured).</summary>
    public void Unpin() => Post(new Unpinned());

    public void SetAutoPlay(bool value) => Post(new AutoPlaySet(value));

    public void SetBroadcasting(bool value) => Post(new BroadcastingSet(value));

    public void SetMasterVolume(float volume) => Post(new MasterVolumeSet(volume));

    public void SetMasterMuted(bool muted) => Post(new MasterMutedSet(muted));

    public void SetPairVolume(ulong ident, float volume) => Post(new PairVolumeSet(ident, volume));

    public void SetPairMuted(ulong ident, bool muted) => Post(new PairMutedSet(ident, muted));

    private void Post(Message message) => mailbox.Writer.TryWrite(message);

    /// <summary>
    /// This publishes a "view" used for the ListeningTab of the UI.
    /// Just to avoid having the UI call methods that lock, since it's all
    /// per-frame stuff.
    /// </summary>
    private void PublishState()
    {
        var list = new List<PairView>(pairs.Count);
        foreach (var (ident, p) in pairs)
        {
            // Keep showing the track being heard while its replacement loads.
            var data = ident == selectedSourceId && ident == appliedSourceId ? appliedData ?? p.Data : p.Data;
            list.Add(new PairView(ident, p.DisplayName, data.Meta, data.FilePath, p.Volume, p.Muted,
                                  ident == selectedSourceId, pinnedName is not null && p.DisplayName == pinnedName));
        }

        published = new PublishedState(list, autoPlay, pinnedName is not null);
    }

    private void OnPlaybackEnded(EngineSessionEnded ended) => Post(new PlaybackEndedMessage(ended));
    private void OnEngineObserved(EngineObservation observation) => Post(new EngineObserved(observation));
    private void OnSessionReconnected() => Post(new EngineReconnected());

    private void Apply(Message message)
    {
        Plugin.Log.Debug("ListeningManager applying {message}", message);
        switch (message)
        {
            case PairUpdated(var ident, var displayName, var data):
                if (pairs.TryGetValue(ident, out var pair))
                {
                    if (!pair.Data.IsPlaying && data.IsPlaying && pair.Data.FilePath == data.FilePath)
                        playbackStartedNotifications.Add(ident);
                    pair.Data = data;
                    pair.FailCount = 0;
                    if (displayName is not null) pair.DisplayName = displayName;
                }
                else
                {
                    newPairNotifications.Add(ident);
                    pairs[ident] = new PairState(data)
                    {
                        DisplayName = displayName,
                        Volume = displayName is not null ? preferredVolumes.GetValueOrDefault(displayName, 1f) : 1f,
                    };
                }

                break;

            case PairCleared(var ident):
                pairs.Remove(ident);
                newPairNotifications.Remove(ident);
                playbackStartedNotifications.Remove(ident);
                if (appliedSourceId == ident)
                {
                    appliedSourceId = null;
                    appliedData = null;
                    playbackTarget = null;
                }

                break;

            case ActiveSet(var ident):
                if (pairs.TryGetValue(ident, out var toPin) && toPin.DisplayName is { } name)
                    pinnedName = name;
                break;

            case Unpinned:
                pinnedName = null;
                break;

            case AutoPlaySet(var value):
                autoPlay = value;
                break;

            case BroadcastingSet(var value):
                broadcasting = value;
                break;

            case MasterVolumeSet(var value):
                masterVolume = Math.Clamp(value, 0f, 1f);
                break;

            case MasterMutedSet(var value):
                masterMuted = value;
                break;

            case PairVolumeSet(var ident, var value):
                if (!pairs.TryGetValue(ident, out var volumePair)) break;
                volumePair.Volume = Math.Clamp(value, 0f, 1f);
                if (volumePair.DisplayName is { } volumeName)
                    preferredVolumes[volumeName] = volumePair.Volume;
                break;

            case PairMutedSet(var ident, var value):
                if (pairs.TryGetValue(ident, out var mutePair)) mutePair.Muted = value;
                break;

            case PlaybackEndedMessage(var ended):
                if (engineTarget?.Revision != ended.Revision) break;
                if (PeekActive() is not { } activeId || !pairs.TryGetValue(activeId, out var active))
                    break;
                switch (ended.Reason)
                {
                    case EndReason.Disconnected:
                        appliedSourceId = null;
                        appliedData = null;
                        engineTarget = null;
                        MarkPlayback(ListenerPlaybackStatus.Loading);
                        break;
                    case EndReason.Failed:
                    {
                        active.FailCount++;
                        if (active.FailCount <= MaxLoadRetries && appliedSourceId == activeId)
                        {
                            appliedSourceId = null;
                            appliedData = null;
                            MarkPlayback(ListenerPlaybackStatus.Loading);
                        }
                        else MarkPlayback(ListenerPlaybackStatus.Failed);

                        break;
                    }
                    case EndReason.Finished:
                    default:
                        active.FailCount = 0;
                        MarkPlayback(ListenerPlaybackStatus.Ended);
                        break;
                }

                break;

            case EngineReconnected:
                // A fresh host has neither our track nor our volume.
                appliedSourceId = null;
                appliedData = null;
                engineTarget = null;
                if (playbackTarget is not null) MarkPlayback(ListenerPlaybackStatus.Loading);
                break;
        }
    }

    private async Task MessageLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
            try
            {
                if (!await mailbox.Reader.WaitToReadAsync(token)) break;
                var reconcile = false;
                while (mailbox.Reader.TryRead(out var message))
                    switch (message)
                    {
                        case EngineObserved(var observation):
                            if (engineTarget?.Revision == observation.Revision ||
                                (engineTarget is null && observation.Snapshot.State == PlaybackState.Stopped))
                                PublishEngineSnapshot(observation.Snapshot);
                            break;
                        default:
                            Apply(message);
                            reconcile = true;
                            break;
                    }

                if (reconcile)
                {
                    await ReconcileActive(token);
                    FlushPairNotifications();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in ListeningManager message loop");
            } finally
            {
                PublishState();
            }
    }

    private ulong? appliedSourceId;
    private PairData? appliedData;
    private PlaybackTarget? playbackTarget;
    private EngineTarget? engineTarget;
    private long nextEngineRevision;

    private async Task ReconcileActive(CancellationToken token)
    {
        var previousAppliedSourceId = appliedSourceId;
        var previousAppliedData = appliedData;
        var activeId = ResolveActive();
        selectedSourceId = activeId;
        Plugin.Log.Debug("selected source: {selectedSourceId}", selectedSourceId ?? 0);

        PairState? active = null;
        if (activeId is { } aid)
            active = pairs[aid];

        EngineSnapshot snapshot;
        try
        {
            snapshot = await engineSession.RefreshAsync(token);
        }
        catch (ConnectionLostException)
        {
            // Happens during startup and if the listening host restarts. Otherwise, this logs an error
            // which annoys me ("Pulsar is producing errors..." notification from Dalamud).
            // OnEngineReconnected will trigger a fresh reconcile once the host is available.
            Plugin.Log.Debug("Listening reconciliation deferred: waiting for the audio host to connect");
            return;
        }

        Plugin.Log.Debug("engine snapshot: {snapshot}", snapshot);
        PublishEngineSnapshot(snapshot);
        var current = activeId == appliedSourceId ? appliedData : null;
        var action = SyncDecider.Decide(current, active?.Data, snapshot);
        Plugin.Log.Debug("decided action: {action}", action);

        if (action is EngineAction.Wait)
        {
            // The pending cursor is deliberately not applied yet. Controls may still
            // change during the grace period, but ReplayGain belongs to the old track.
            if (active is not null && current is not null)
                ApplyActiveVolume(active, current);
            return;
        }

        SetPlaybackTarget(activeId, active);
        if (active is not null)
            ApplyActiveVolume(active, active.Data);
        Apply(action);
        appliedSourceId = activeId;
        appliedData = active?.Data;
        MaybeNotifyTrackChanged(previousAppliedSourceId, previousAppliedData);
    }

    private void FlushPairNotifications()
    {
        foreach (var ident in newPairNotifications)
        {
            if (!pairs.TryGetValue(ident, out var pair)) continue;
            NotifyNewPair(ident, pair);
        }

        foreach (var ident in playbackStartedNotifications)
        {
            if (newPairNotifications.Contains(ident) ||
                ident != selectedSourceId ||
                !pairs.TryGetValue(ident, out var pair) ||
                SilenceReason(pair) is not { } reason)
                continue;
            outputs.Writer.TryWrite(new ListeningOutput.SilentPlaybackStarted(PairName(ident, pair), reason));
        }

        newPairNotifications.Clear();
        playbackStartedNotifications.Clear();
    }

    private void NotifyNewPair(ulong ident, PairState pair)
    {
        var name = PairName(ident, pair);

        if (ident == selectedSourceId && pair.Data.IsPlaying && SilenceReason(pair) is { } reason)
        {
            outputs.Writer.TryWrite(new ListeningOutput.SilentPlaybackStarted(name, reason));
            return;
        }

        outputs.Writer.TryWrite(new ListeningOutput.NearbyBroadcastDetected(
                                    Track(name, pair.Data), ClassifyNearbyBroadcast(ident)));
    }

    private NearbyBroadcastContext ClassifyNearbyBroadcast(ulong ident)
    {
        // Debug loopback and pins should use the normal notification path
        if (ident == ulong.MaxValue || pinnedName is not null)
            return NearbyBroadcastContext.Normal;
        if (broadcasting) return NearbyBroadcastContext.CurrentlyBroadcasting;
        if (!autoPlay) return NearbyBroadcastContext.AutoPlayOff;
        return NearbyBroadcastContext.Normal;
    }

    private void MaybeNotifyTrackChanged(ulong? previousSourceId, PairData? previousData)
    {
        if (previousSourceId is not { } sourceId ||
            sourceId != appliedSourceId ||
            previousData is null ||
            appliedData is null ||
            previousData.FilePath == appliedData.FilePath ||
            !pairs.TryGetValue(sourceId, out var pair))
            return;

        outputs.Writer.TryWrite(new ListeningOutput.TrackChanged(Track(PairName(sourceId, pair), appliedData)));
    }

    private ListeningSilenceReason? SilenceReason(PairState pair)
    {
        if (masterMuted) return ListeningSilenceReason.MasterMuted;
        if (pair.Muted) return ListeningSilenceReason.PairMuted;
        if (masterVolume <= 0f) return ListeningSilenceReason.MasterVolumeZero;
        if (pair.Volume <= 0f) return ListeningSilenceReason.PairVolumeZero;
        return null;
    }

    private static string PairName(ulong ident, PairState pair) => pair.DisplayName ?? $"{ident:X}";

    private static ListeningTrack Track(string sourceName, PairData data) => new(sourceName, data.FilePath, data.Meta);

    private void SetPlaybackTarget(ulong? activeId, PairState? active)
    {
        if (activeId is not { } sourceId || active is null)
        {
            playbackTarget = null;
            PublishPlayback(new ListenerPlaybackView(ListenerPlaybackStatus.Idle, null, null));
            return;
        }

        var target = new PlaybackTarget(sourceId, active.Data.CursorEpoch, active.Data.FilePath);
        if (target == playbackTarget) return;
        playbackTarget = target;
        PublishPlayback(new ListenerPlaybackView(ListenerPlaybackStatus.Loading, target.Path, null));
    }

    private void MarkPlayback(ListenerPlaybackStatus status)
    {
        PublishPlayback(playback with { Status = status, Position = null });
    }

    private void PublishEngineSnapshot(EngineSnapshot snapshot)
    {
        var current = playback;
        var isListening = current.TargetPath is not null &&
                          snapshot.Path == current.TargetPath &&
                          snapshot.State == PlaybackState.Playing;
        if (lastListening != isListening)
        {
            lastListening = isListening;
            outputs.Writer.TryWrite(new ListeningOutput.ListeningChanged(isListening));
        }

        ListenerPlaybackView next;
        if (current.TargetPath is null)
            next = new ListenerPlaybackView(ListenerPlaybackStatus.Idle, null, null);
        else if (snapshot.Path != current.TargetPath)
        {
            next = current.Status is ListenerPlaybackStatus.Ended or ListenerPlaybackStatus.Failed
                       ? current
                       : current with { Status = ListenerPlaybackStatus.Loading, Position = null };
        }
        else
        {
            next = snapshot.State switch
            {
                PlaybackState.Playing => current with
                {
                    Status = ListenerPlaybackStatus.Playing,
                    Position = snapshot.Position,
                },
                PlaybackState.Paused => current with
                {
                    Status = ListenerPlaybackStatus.Paused,
                    Position = snapshot.Position,
                },
                _ => current with { Status = ListenerPlaybackStatus.Loading, Position = snapshot.Position },
            };
        }

        PublishPlayback(next);
    }

    private void PublishPlayback(ListenerPlaybackView value) => Volatile.Write(ref playback, value);

    /// <summary>
    /// Contains the "which source should actually be playing" logic.
    /// </summary>
    private ulong? ResolveActive()
    {
        if (pinnedName is not null || broadcasting) return PeekActive();
        if (!autoPlay)
        {
            // "Off" means silence: a lingering autoplay pick must not keep playing.
            autoplaySourceId = null;
            return null;
        }

        // If the current pick still exists, keep it
        if (PeekActive() is { } current) return current;
        autoplaySourceId = PickNextAutoplay();
        return autoplaySourceId;
    }

    private ulong? PeekActive()
    {
        if (pinnedName is { } name)
        {
            foreach (var (id, p) in pairs)
                if (p.DisplayName == name)
                    return id;
            return null;
        }

        if (broadcasting || !autoPlay) return null;
        return autoplaySourceId is { } current && pairs.ContainsKey(current) ? current : null;
    }

    /// <summary>
    /// With the way this class does reconciliation/resolution, we need autoplay to
    /// choose sources in a stable way. Otherwise, you might end up flapping between
    /// sources, if the source order changes for some reason. This method just picks
    /// the lowest ID, aka the lowest address in the object table. That's not *quite*
    /// random from a user's perspective - it's likely going to be who's been loaded
    /// in the longest, OR someone who loaded in after someone who's been loaded in
    /// a while leaves (freeing up a slot). Assuming the game doesn't "compact" the
    /// object table (which... I don't know, honestly).
    ///
    /// Anyway. This is just to force a stable autoplay pick.
    /// </summary>
    private ulong? PickNextAutoplay()
    {
        ulong? best = null;
        foreach (var id in pairs.Keys)
            if (best is null || id < best)
                best = id;
        return best;
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private float lastVolumeSet = -1f;

    private void ApplyActiveVolume(PairState pair, PairData track)
    {
        var rawRg = track.Meta?.ReplayGainDb ?? 0.0;
        // Stop a peer from sending ridiculous gain values.
        // (Note, though: we clamp our total gain stage to 1.0 anyway.)
        var rgDb = double.IsFinite(rawRg) ? Math.Clamp(rawRg, -20.0, 20.0) : 0.0;
        var effective = masterMuted || pair.Muted ? 0f : masterVolume * pair.Volume * DbToLinear(rgDb);

        if (float.IsFinite(effective) && effective == lastVolumeSet) return; // make method idempotent
        engineSession.SetVolume(effective);
        lastVolumeSet = effective;
    }

    private void Apply(EngineAction action)
    {
        switch (action)
        {
            case EngineAction.Load l:
                engineTarget = new EngineTarget(++nextEngineRevision, l.Path,
                                                l.Playing ? PlaybackState.Playing : PlaybackState.Paused, l.Position);
                engineSession.SetTarget(engineTarget);
                break;
            case EngineAction.Seek s:
                engineSession.Seek(s.Position);
                break;
            case EngineAction.Pause:
                if (engineTarget is not null)
                {
                    engineTarget = engineTarget with { State = PlaybackState.Paused };
                    engineSession.SetTarget(engineTarget);
                }

                break;
            case EngineAction.Resume:
                if (engineTarget is not null)
                {
                    engineTarget = engineTarget with { State = PlaybackState.Playing };
                    engineSession.SetTarget(engineTarget);
                }

                break;
            case EngineAction.Stop:
                engineTarget = null;
                engineSession.SetTarget(null);
                break;
        }
    }
}
