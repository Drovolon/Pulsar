using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
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

internal class PairState(PairData desired)
{
    internal PairData? Current;           // last applied state
    internal PairData? Desired = desired; // latest desired state
    internal string? DisplayName;         // resolved character name; null if the address couldn't be resolved
    internal float Volume = 1f;
    internal bool Muted;
    internal int FailCount; // consecutive load failures for the current desired state
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

/// <summary>
/// ListeningManager coordinates listening to songs streamed from other players, via IPC.
/// It drives a single playback engine, fed by at most one "active" source at a time.
///
/// After a lot of thought, this class works like this. UnsafeResolveActive() picks WHICH
/// source should be actually playing audio, right now, by looking at whom the user has pinned
/// (if anyone), whether the user is broadcasting, and whether the user has autoplay enabled.
///
/// On the flip side, SyncDecider.Decide() picks HOW to make the engine match that source's
/// desired state. Like, whether to load a new song, seek in the current song, stop playing, etc.
///
/// Reconciliation is event based. Every pair update or control change calls Reevaluate, which
/// does both source resolution AND engine updates.
/// </summary>
public class ListeningManager : IAsyncDisposable
{
    private const int MaxLoadRetries = 3;

    private readonly Dictionary<ulong, PairState> pairs = [];
    private readonly IRemoteEngine engine;
    private readonly SemaphoreSlim stateLock = new(1, 1);

    // To avoid exposing callers to `stateLock`, all calls are funneled through this queue,
    // which is drained by the reconcile loop. This preserves operation order, prevents races,
    // and stops callers from accidentally deadlocking themselves. (Even if it was hard to do so.)
    private readonly ConcurrentQueue<Action> mutations = new();

    private void EnqueueMutation(Action mutate)
    {
        mutations.Enqueue(mutate);
        ScheduleReevaluate();
    }

    private float masterVolume;
    private bool masterMuted;

    // True when broadcasting. While broadcasting, we never tune into a nearby broadcaster.
    private volatile bool broadcasting;

    // Pinned character name. This is the user selecting "I want to listen to John Finalfantasy".
    // Note, pins aren't saved in config, so they won't survive a plugin reload etc.
    private volatile string? pinnedName;

    // Which pair is currently playing, if any. Only meaningful while AutoPlay is on
    // and the pair still exists; UnsafeResolveActive re-derives it otherwise.
    private ulong? currentActiveId;

    // Per-source volumes, a map of character name -> volume. Persisted in config by the UI code.
    // Then the UI code calls into this class to push down updates.
    private readonly Dictionary<string, float> preferredVolumes;

    private volatile IReadOnlyList<PairView> view = []; // maintained by UnsafePublishView()
    /// <summary>View for UI code, so it doesn't need to take locks.</summary>
    public IReadOnlyList<PairView> View => view;

    /// <summary>Live position of the active source (the ticking progress bar). Lock-free.</summary>
    public PlaybackPosition? ActivePosition { get; private set; }

    /// <summary>Whether the active source is currently producing sound. Lock-free.</summary>
    public bool ActiveNowPlaying { get; private set; }

    /// <summary>
    /// Whether to play nearby broadcasters automatically. Off means silence, and stops
    /// autoplay. A pin overrides all this.
    /// </summary>
    public bool AutoPlay => autoPlay;
    private volatile bool autoPlay;

    /// <summary>Whether anyone is pinned.</summary>
    public bool HasPin => pinnedName is not null;

    private readonly SemaphoreSlim reevaluateSignal = new(0);
    private readonly Task reevaluateLoop;

    private readonly Task updateLoop;
    
    private readonly CancellationTokenSource asyncCts  = new();

    private readonly TimeSpan updatePoll;
    private readonly TimeSpan errorBackoff;
    private readonly TimeSpan lostBackoff;

    public ListeningManager(IRemoteEngine engine, float masterVolume = 1f,
                            IReadOnlyDictionary<string, float>? savedPairVolumes = null, bool autoPlay = true)
        : this(engine, masterVolume, savedPairVolumes, autoPlay,
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
        this.engine.OnPlaybackEnded += OnPlaybackEnded;

        reevaluateLoop = Task.Run(() => ReevaluateRunLoop(asyncCts.Token));
        updateLoop = Task.Run(() => UpdateLoop(asyncCts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        engine.OnPlaybackEnded -= OnPlaybackEnded;
        asyncCts.Cancel();
        await reevaluateLoop;
        await updateLoop;
    }

    /// <summary>
    /// Called after the audio host reconnects.
    /// </summary>
    public void OnEngineReconnected()
    {
        // after reset, volume will prob be at 1f host-side; reset it so reevaluate re-applies the correct value
        lastVolumeSet = -1f;
        ScheduleReevaluate();
    }

    public void AddOrUpdatePair(ulong ident, string? displayName, PairData data) => EnqueueMutation(() =>
    {
        if (pairs.TryGetValue(ident, out var pair))
        {
            pair.Desired = data;
            pair.FailCount = 0;
            if (displayName is not null) pair.DisplayName = displayName;
        }
        else
        {
            pairs[ident] = new PairState(data)
            {
                DisplayName = displayName,
                Volume = displayName != null ? preferredVolumes.GetValueOrDefault(displayName, 1f) : 1f,
            };
        }
    });

    public void ClearPair(ulong ident) => EnqueueMutation(() =>
    {
        // Note: if this was the active source, reconcile will stop the playback engine for us.
        pairs.Remove(ident);
    });

    /// <summary>Pin a specific source. Ignored if the address can't be resolved to a character name.</summary>
    public void SetActive(ulong ident) => EnqueueMutation(() =>
    {
        if (!pairs.TryGetValue(ident, out var p)) return;
        // Name is required to persist volumes to config.
        if (p.DisplayName is not { } name) return;
        pinnedName = name;
    });

    /// <summary>Unpin makes us fall back to either autoplay or off (depending on what's configured).</summary>
    public void Unpin() => EnqueueMutation(() => pinnedName = null);

    public void SetAutoPlay(bool value) => EnqueueMutation(() => autoPlay = value);

    public void SetBroadcasting(bool value)
    {
        if (broadcasting == value) return;
        broadcasting = value;
        ScheduleReevaluate();
        // Note: Reevaluate will stop playback automatically while broadcasting.
    }

    public void SetMasterVolume(float volume) => EnqueueMutation(() =>
        masterVolume = Math.Clamp(volume, 0f, 1f));

    public void SetMasterMuted(bool muted) => EnqueueMutation(() => masterMuted = muted);

    public void SetPairVolume(ulong ident, float volume) => EnqueueMutation(() =>
    {
        if (!pairs.TryGetValue(ident, out var p)) return;
        p.Volume = Math.Clamp(volume, 0f, 1f);
        if (p.DisplayName is { } name) preferredVolumes[name] = p.Volume;
    });

    public void SetPairMuted(ulong ident, bool muted) => EnqueueMutation(() =>
    {
        if (!pairs.TryGetValue(ident, out var p)) return;
        p.Muted = muted;
    });

    /// <summary>
    /// This publishes a "view" used for the ListeningTab of the UI.
    /// Just to avoid having the UI call methods that lock, since it's all
    /// per-frame stuff.
    /// </summary>
    private void UnsafePublishView()
    {
        var activeId = UnsafeResolveActive();
        var list = new List<PairView>(pairs.Count);
        foreach (var (ident, p) in pairs)
        {
            var data = p.Desired ?? p.Current;
            list.Add(new PairView(
                         ident,
                         p.DisplayName,
                         data?.Meta,
                         data?.FilePath,
                         p.Volume,
                         p.Muted,
                         ident == activeId,
                         pinnedName is not null && p.DisplayName == pinnedName));
        }
        view = list;
    }

    /// <summary>
    /// Polls the playback engine every 50ms to get the current position and state.
    /// This is used for the UI, strictly.
    /// </summary>
    private async Task UpdateLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var state = await engine.GetStateAsync(token);
                ActivePosition = state.Position;
                ActiveNowPlaying = state.State == PlaybackState.Playing;
                await Task.Delay(updatePoll, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (TaskCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ConnectionLostException)
            {
                Plugin.Log.Warning("Connection to audio host lost");
                // back off
                try { await Task.Delay(lostBackoff, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error in ListeningManager UpdateLoop");
                // back off
                try { await Task.Delay(errorBackoff, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
        }
    }

    private void OnPlaybackEnded(object? _, EndReason reason) => EnqueueMutation(() =>
    {
        if (UnsafePeekActive() is not { } id) return;
        var active = pairs[id];

        if (reason == EndReason.Failed)
        {
            active.FailCount++;
            if (active.FailCount <= MaxLoadRetries)
                active.Current = null;
        }
        else
        {
            active.FailCount = 0;
        }
    });

    private void ScheduleReevaluate()
    {
        reevaluateSignal.Release();
    }

    private async Task ReevaluateRunLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await reevaluateSignal.WaitAsync(token);

                await stateLock.WaitAsync(token);
                try
                {
                    while (mutations.TryDequeue(out var mutate)) mutate();
                    await UnsafeReconcileActive();
                    UnsafePublishView();
                }
                finally
                {
                    stateLock.Release();
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
                Plugin.Log.Error(ex, "Error in ListeningManager ReevaluateRunLoop");
            }
        }
    }

    // unsafe methods assume lock(@lock) held by caller

    private async Task UnsafeReconcileActive()
    {
        var activeId = UnsafeResolveActive();

        PairState? active = null;
        if (activeId is { } aid)
        {
            await UnsafeApplyActiveVolume();
            active = pairs[aid];
        }

        var snapshot = await engine.GetStateAsync(asyncCts.Token);
        var action = SyncDecider.Decide(active?.Current, active?.Desired, snapshot);
        if (action is EngineAction.Wait) return;

        await UnsafeApply(action);
        if (action is EngineAction.Load) await UnsafeApplyActiveVolume();
        active?.Current = active.Desired;
    }

    /// <summary>
    /// Contains the "which source should actually be playing" logic.
    /// </summary>
    private ulong? UnsafeResolveActive()
    {
        if (pinnedName is not null || broadcasting) return UnsafePeekActive();
        if (!AutoPlay)
        {
            // "Off" means silence: a lingering autoplay pick must not keep playing.
            currentActiveId = null;
            return null;
        }
        // If the current pick still exists, keep it
        if (UnsafePeekActive() is { } current) return current;
        currentActiveId = UnsafePickNextAutoplay();
        return currentActiveId;
    }

    private ulong? UnsafePeekActive()
    {
        if (pinnedName is { } name)
        {
            foreach (var (id, p) in pairs)
                if (p.DisplayName == name) return id;
            return null;
        }
        if (broadcasting || !AutoPlay) return null;
        return currentActiveId is { } current && pairs.ContainsKey(current) ? current : null;
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
    private ulong? UnsafePickNextAutoplay()
    {
        ulong? best = null;
        foreach (var id in pairs.Keys)
            if (best is null || id < best) best = id;
        return best;
    }

    private static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);

    private float lastVolumeSet = -1f;
    private async Task UnsafeApplyActiveVolume()
    {
        if (UnsafeResolveActive() is not { } id) return;
        var p = pairs[id];
        var rawRg = (p.Desired ?? p.Current)?.Meta?.ReplayGainDb ?? 0.0;
        // Stop a peer from sending ridiculous gain values.
        // (Note, though: we clamp our total gain stage to 1.0 anyway.)
        var rgDb = double.IsFinite(rawRg) ? Math.Clamp(rawRg, -20.0, 20.0) : 0.0;
        var effective = masterMuted || p.Muted ? 0f : masterVolume * p.Volume * DbToLinear(rgDb);
        
        if (float.IsFinite(effective) && effective == lastVolumeSet) return; // make method idempotent
        await engine.SetVolumeAsync(effective, asyncCts.Token);
        lastVolumeSet = effective;
    }

    private async Task UnsafeApply(EngineAction action)
    {
        switch (action)
        {
            case EngineAction.Load l:
                await engine.LoadFileAsync(l.Path, l.Position, l.Playing, asyncCts.Token);
                break;
            case EngineAction.Seek s:
                await engine.SeekAsync(s.Position, asyncCts.Token);
                break;
            case EngineAction.Pause:
                await engine.PauseAsync(asyncCts.Token);
                break;
            case EngineAction.Resume:
                await engine.ResumeAsync(asyncCts.Token);
                break;
            case EngineAction.Stop:
                await engine.StopAsync(asyncCts.Token);
                break;
        }
    }
}
