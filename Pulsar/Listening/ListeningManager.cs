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
    int CursorEpoch,
    TrackMeta? Meta
);

internal class PairState(PairData data)
{
    internal PairData Data = data;
    internal string? DisplayName;         // resolved character name; null if the address couldn't be resolved
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

public enum ListenerPlaybackStatus { Idle, Loading, Playing, Paused, Ended, Failed }

/// <summary>Atomic UI-facing state of the one listening playback engine.</summary>
public sealed record ListenerPlaybackView(
    ListenerPlaybackStatus Status,
    string? TargetPath,
    PlaybackPosition? Position);

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
    private sealed record PlaybackEnded(EndReason Reason) : Message;
    private sealed record EngineChanged(EngineSnapshot Snapshot) : Message;
    private sealed record EngineReconnected : Message;
    private sealed record PollTick(long Generation) : Message;
    private sealed record PollCompleted(
        long Generation,
        EngineSnapshot? Snapshot,
        Exception? Error) : Message;
    private sealed record PlaybackTarget(ulong SourceId, int CursorEpoch, string Path);

    private sealed record PublishedState(
        IReadOnlyList<PairView> View,
        bool AutoPlay,
        bool HasPin);

    private readonly Dictionary<ulong, PairState> pairs = [];
    private readonly IRemoteEngine engine;
    // This is the manager's synchronization boundary: callers and engine callbacks only post
    // messages; the single reader below is the sole owner of all non-published state.
    private readonly Channel<Message> mailbox = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<ListeningOutput> outputs = Channel.CreateUnbounded<ListeningOutput>(
        new UnboundedChannelOptions { SingleReader = true });
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
    private readonly CancellationTokenSource asyncCts  = new();
    private CancellationTokenSource? pollDelayCts;
    private Task? pollDelayTask;
    private Task? pollTask;
    private long pollGeneration;

    private readonly TimeSpan updatePoll;
    private readonly TimeSpan errorBackoff;
    private readonly TimeSpan lostBackoff;

    public ListeningManager(IRemoteEngine engine, Configuration config)
        : this(engine, config.ListeningMasterVolume, config.ListeningPairVolumes,
               config.ListeningAutoPlay,
               updatePoll: TimeSpan.FromMilliseconds(250),
               errorBackoff: TimeSpan.FromSeconds(1),
               lostBackoff: TimeSpan.FromSeconds(10)) { }

    // for unit tests only
    internal ListeningManager(IRemoteEngine engine, float masterVolume,
                              IReadOnlyDictionary<string, float>? savedPairVolumes, bool autoPlay,
                              TimeSpan updatePoll, TimeSpan errorBackoff, TimeSpan lostBackoff)
    {
        this.updatePoll = updatePoll;
        this.errorBackoff = errorBackoff;
        this.lostBackoff = lostBackoff;
        this.engine = engine;
        this.masterVolume = masterVolume;
        preferredVolumes = savedPairVolumes is null ? [] : new Dictionary<string, float>(savedPairVolumes);
        this.autoPlay = autoPlay;
        published = new PublishedState([], autoPlay, false);
        this.engine.OnPlaybackEnded += OnPlaybackEnded;
        this.engine.OnChanged += OnEngineChanged;

        messageLoop = MessageLoop(asyncCts.Token);
        Post(new PollTick(pollGeneration));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        engine.OnPlaybackEnded -= OnPlaybackEnded;
        engine.OnChanged -= OnEngineChanged;
        asyncCts.Cancel();
        mailbox.Writer.TryComplete();
        await messageLoop;
        if (pollTask is not null) await pollTask;
        await CancelPollDelay();
        asyncCts.Dispose();
        outputs.Writer.TryComplete();
    }

    /// <summary>
    /// Called after the audio host reconnects.
    /// </summary>
    public void OnEngineReconnected() => Post(new EngineReconnected());

    public void AddOrUpdatePair(ulong ident, string? displayName, PairData data)
        => Post(new PairUpdated(ident, displayName, data));

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
            // A newer cursor may be pending while the applied track finishes its outro.
            // Keep the active row on what the listener is actually hearing until the
            // pending cursor is committed.
            var data = ident == selectedSourceId && ident == appliedSourceId
                ? appliedData ?? p.Data
                : p.Data;
            list.Add(new PairView(
                         ident,
                         p.DisplayName,
                         data.Meta,
                         data.FilePath,
                         p.Volume,
                         p.Muted,
                         ident == selectedSourceId,
                         pinnedName is not null && p.DisplayName == pinnedName));
        }
        published = new PublishedState(list, autoPlay, pinnedName is not null);
    }

    private async Task PollEngine(long generation, CancellationToken token)
    {
        EngineSnapshot? snapshot = null;
        Exception? error = null;
        try
        {
            snapshot = await engine.GetStateAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            error = ex;
        }

        Post(new PollCompleted(generation, snapshot, error));
    }

    private void OnPlaybackEnded(object? _, EndReason reason) => Post(new PlaybackEnded(reason));
    private void OnEngineChanged(object? _, EngineSnapshot snapshot) => Post(new EngineChanged(snapshot));

    private async ValueTask SchedulePoll(TimeSpan delay)
    {
        await CancelPollDelay();
        var generation = ++pollGeneration;
        if (delay <= TimeSpan.Zero)
        {
            Post(new PollTick(generation));
            return;
        }

        pollDelayCts = CancellationTokenSource.CreateLinkedTokenSource(asyncCts.Token);
        pollDelayTask = PostPollAfterDelay(delay, generation, pollDelayCts.Token);
    }

    private async Task PostPollAfterDelay(TimeSpan delay, long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            Post(new PollTick(generation));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async ValueTask CancelPollDelay()
    {
        pollDelayCts?.Cancel();
        if (pollDelayTask is not null) await pollDelayTask;
        pollDelayCts?.Dispose();
        pollDelayCts = null;
        pollDelayTask = null;
    }

    private void StartPoll(long generation, CancellationToken token)
    {
        if (generation != pollGeneration || pollTask is not null) return;
        pollDelayCts?.Dispose();
        pollDelayCts = null;
        pollDelayTask = null;
        pollTask = PollEngine(generation, token);
    }

    private async ValueTask HandlePollCompleted(
        long generation,
        EngineSnapshot? snapshot,
        Exception? error)
    {
        if (generation != pollGeneration) return;
        pollTask = null;
        switch (error)
        {
            case null:
                if (snapshot is not null) PublishEngineSnapshot(snapshot);
                await SchedulePoll(updatePoll);
                break;
            case ConnectionLostException:
                Plugin.Log.Warning("Connection to audio host lost");
                await SchedulePoll(lostBackoff);
                break;
            default:
                Plugin.Log.Error(error, "Error polling the listening engine");
                await SchedulePoll(errorBackoff);
                break;
        }
    }

    private void Apply(Message message)
    {
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
                        Volume = displayName is not null
                            ? preferredVolumes.GetValueOrDefault(displayName, 1f)
                            : 1f,
                    };
                }
                break;

            case PairCleared(var ident):
                pairs.Remove(ident);
                newPairNotifications.Remove(ident);
                playbackStartedNotifications.Remove(ident);
                break;

            case ActiveSet(var ident):
                if (pairs.TryGetValue(ident, out var toPin)
                    && toPin.DisplayName is { } name)
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

            case PlaybackEnded(var reason):
                if (PeekActive() is not { } activeId || !pairs.TryGetValue(activeId, out var active))
                    break;
                if (reason == EndReason.Failed)
                {
                    active.FailCount++;
                    if (active.FailCount <= MaxLoadRetries && appliedSourceId == activeId)
                    {
                        appliedSourceId = null;
                        appliedData = null;
                        MarkPlayback(ListenerPlaybackStatus.Loading);
                    }
                    else MarkPlayback(ListenerPlaybackStatus.Failed);
                }
                else
                {
                    active.FailCount = 0;
                    MarkPlayback(ListenerPlaybackStatus.Ended);
                }
                break;

            case EngineReconnected:
                // A fresh host has neither our track nor our volume.
                appliedSourceId = null;
                appliedData = null;
                lastVolumeSet = -1f;
                if (playbackTarget is not null) MarkPlayback(ListenerPlaybackStatus.Loading);
                break;
        }
    }

    private async Task MessageLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!await mailbox.Reader.WaitToReadAsync(token)) break;
                var reconcile = false;
                while (mailbox.Reader.TryRead(out var message))
                {
                    switch (message)
                    {
                        case EngineChanged(var snapshot):
                            PublishEngineSnapshot(snapshot);
                            break;
                        case PollTick(var generation):
                            StartPoll(generation, token);
                            break;
                        case PollCompleted(var generation, var snapshot, var error):
                            await HandlePollCompleted(generation, snapshot, error);
                            break;
                        case EngineReconnected:
                            Apply(message);
                            if (pollTask is null) await SchedulePoll(TimeSpan.Zero);
                            reconcile = true;
                            break;
                        default:
                            Apply(message);
                            reconcile = true;
                            break;
                    }
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
            catch (ConnectionLostException)
            {
                Plugin.Log.Warning("Connection to audio host lost during reconcile");
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in ListeningManager message loop");
            }
            finally
            {
                PublishState();
            }
        }
    }

    private ulong? appliedSourceId;
    private PairData? appliedData;
    private PlaybackTarget? playbackTarget;

    private async Task ReconcileActive(CancellationToken token)
    {
        var previousAppliedSourceId = appliedSourceId;
        var previousAppliedData = appliedData;
        var activeId = ResolveActive();
        selectedSourceId = activeId;

        PairState? active = null;
        if (activeId is { } aid)
            active = pairs[aid];

        var snapshot = await engine.GetStateAsync(token);
        PublishEngineSnapshot(snapshot);
        var current = activeId == appliedSourceId ? appliedData : null;
        var action = SyncDecider.Decide(current, active?.Data, snapshot);

        if (action is EngineAction.Wait)
        {
            // The pending cursor is deliberately not applied yet. Controls may still
            // change during the grace period, but ReplayGain belongs to the old track.
            if (active is not null && current is not null)
                await ApplyActiveVolume(active, current, token);
            return;
        }

        SetPlaybackTarget(activeId, active);
        if (active is not null)
            await ApplyActiveVolume(active, active.Data, token);
        await Apply(action, token);
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
            if (newPairNotifications.Contains(ident) || ident != selectedSourceId
                || !pairs.TryGetValue(ident, out var pair)
                || SilenceReason(pair) is not { } reason)
                continue;
            outputs.Writer.TryWrite(new ListeningOutput.SilentPlaybackStarted(
                PairName(ident, pair), reason));
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
        if (previousSourceId is not { } sourceId
            || sourceId != appliedSourceId
            || previousData is null
            || appliedData is null
            || previousData.FilePath == appliedData.FilePath
            || !pairs.TryGetValue(sourceId, out var pair))
            return;

        outputs.Writer.TryWrite(new ListeningOutput.TrackChanged(
            Track(PairName(sourceId, pair), appliedData)));
    }

    private ListeningSilenceReason? SilenceReason(PairState pair)
    {
        if (masterMuted) return ListeningSilenceReason.MasterMuted;
        if (pair.Muted) return ListeningSilenceReason.PairMuted;
        if (masterVolume <= 0f) return ListeningSilenceReason.MasterVolumeZero;
        if (pair.Volume <= 0f) return ListeningSilenceReason.PairVolumeZero;
        return null;
    }

    private static string PairName(ulong ident, PairState pair)
        => pair.DisplayName ?? $"{ident:X}";

    private static ListeningTrack Track(string sourceName, PairData data)
        => new(sourceName, data.FilePath, data.Meta);

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
        var isListening = current.TargetPath is not null
                          && snapshot.Path == current.TargetPath
                          && snapshot.State == PlaybackState.Playing;
        if (lastListening != isListening)
        {
            lastListening = isListening;
            outputs.Writer.TryWrite(new ListeningOutput.ListeningChanged(isListening));
        }

        ListenerPlaybackView next;
        if (current.TargetPath is null)
        {
            next = new ListenerPlaybackView(ListenerPlaybackStatus.Idle, null, null);
        }
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

    private void PublishPlayback(ListenerPlaybackView value)
        => Volatile.Write(ref playback, value);

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
                if (p.DisplayName == name) return id;
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
            if (best is null || id < best) best = id;
        return best;
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private float lastVolumeSet = -1f;
    private async Task ApplyActiveVolume(PairState pair, PairData track, CancellationToken token)
    {
        var rawRg = track.Meta?.ReplayGainDb ?? 0.0;
        // Stop a peer from sending ridiculous gain values.
        // (Note, though: we clamp our total gain stage to 1.0 anyway.)
        var rgDb = double.IsFinite(rawRg) ? Math.Clamp(rawRg, -20.0, 20.0) : 0.0;
        var effective = masterMuted || pair.Muted
            ? 0f
            : masterVolume * pair.Volume * DbToLinear(rgDb);
        
        if (float.IsFinite(effective) && effective == lastVolumeSet) return; // make method idempotent
        await engine.SetVolumeAsync(effective, token);
        lastVolumeSet = effective;
    }

    private async Task Apply(EngineAction action, CancellationToken token)
    {
        switch (action)
        {
            case EngineAction.Load l:
                await engine.LoadFileAsync(l.Path, l.Position, l.Playing, token);
                break;
            case EngineAction.Seek s:
                await engine.SeekAsync(s.Position, token);
                break;
            case EngineAction.Pause:
                await engine.PauseAsync(token);
                break;
            case EngineAction.Resume:
                await engine.ResumeAsync(token);
                break;
            case EngineAction.Stop:
                await engine.StopAsync(token);
                break;
        }
    }
}
