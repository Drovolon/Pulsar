using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Ipc;
using Timer = System.Threading.Timer;

namespace Pulsar.Broadcast;

// Which source the broadcast tab plays from. Persisted in Configuration.
public enum BroadcastMode { Folder, Mod, Beefweb }

/// <summary>
/// BroadcastManager coordinates the active broadcast source, and integrates with the IPC layer.
/// Note, BroadcastManager only "announces" new broadcast events once SyncPrep is finished with
/// transcoding and ReplayGain calculation. 
/// The announced manifest is <b>eventual</b>: <see cref="Map"/> announces the sync-prepared wire path
/// (transcode + ReplayGain), so a track only reaches the wire once <see cref="SyncPrep"/> has it ready.
/// A transcode gap HOLDS the last manifest (emits nothing) rather than clearing; only a real source stop
/// or an unsyncable track emits null. This is also where the cursorEpoch gets incremented.
/// </summary>
public sealed class BroadcastManager : IAsyncDisposable
{
    private readonly IModResolver penumbra;
    private readonly SyncPrep prep;
    private readonly IRemoteEngine player;
    private readonly Func<Configuration> config;
    private readonly Lock @lock = new();
    private IMusicSource? active;
    private int cursorEpoch;

    // Serializes source switches: SetSource is a multi-await sequence (teardown, then
    // swap+announce) and two interleaved switches would clobber each other's swap.
    private readonly SemaphoreSlim switchLock = new(1, 1);
    private bool disposed; // guarded by switchLock: a SetSource queued behind dispose must not install

    // The active source's OnChanged subscription, bound to that source's identity so a
    // late event from a torn-down source can be told apart from the live one.
    private Action? activeSourceHandler;

    private (string, string[], PulsarCursor)? holdValue;     // last computed manifest; the transcode-gap hold
    private (string, string[], PulsarCursor)? lastAnnounced; // last payload delivered; the dedup comparand

    // The ordered dispatcher: every outward-facing action (subscriber callbacks, timer
    // arming, prep kicks) is enqueued - under @lock where ordering matters - and executed
    // by ONE pump task. Compute order is enqueue order is delivery order, so no seq
    // fences are needed, and subscribers never run under any of our locks: a wedged
    // subscriber stalls only the queue, never a source event or a switch.
    private readonly Channel<Action> deliveries = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task deliveryPump;

    private readonly Timer prefetchTimer;
    private bool prefetchFinalPending;                       // which of the two near-end checkpoints is next

    public BroadcastManager(IRemoteEngine player, IModResolver penumbra, SyncPrep prep, Func<Configuration> config)
    {
        this.penumbra = penumbra;
        this.prep = prep;
        this.config = config;
        this.player = player;
        prefetchTimer = new Timer(_ => OnPrefetchTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        deliveryPump = Task.Run(DeliveryPump);
    }

    private async Task DeliveryPump()
    {
        await foreach (var deliver in deliveries.Reader.ReadAllAsync())
        {
            try { deliver(); }
            catch (Exception e) { Plugin.Log.Error(e, "broadcast delivery failed"); }
        }
    }

    /// <summary>Fires when something changes. null means stopped. Includes (currentFile, prefetchFiles, cursor).</summary>
    public event Action<(string, string[], PulsarCursor)?>? OnPlayerDataChanged;

    /// <summary>
    /// Fires when broadcasting toggles.
    /// </summary>
    public event Action<bool>? OnBroadcastingChanged;
    private bool lastBroadcasting;

    /// <summary>The active source IF they are using the folder or mod player (not beefweb).</summary>
    public Jukebox? ActiveJukebox => active as Jukebox;

    /// <summary>The active source IF it is the beefweb watcher.</summary>
    public Beefweb.Watcher? ActiveBeefweb => active as Beefweb.Watcher;

    /// <summary>The active source's live snapshot, source-agnostic (for the shared now-playing line).</summary>
    public SourceSnapshot? CurrentSnapshot { get { lock (@lock) return active?.Current; } }

    /// <summary>
    /// Reapply volume after reconnect, since a new process will start with volume=1 (max).
    /// </summary>
    public void OnEngineReconnected() => ActiveJukebox?.Player.ReapplyVolume();

    /// <summary>Broadcast from a local folder on disk.</summary>
    public Task LoadFolder(string directory) => LoadJukebox(directory);

    /// <summary>Broadcast from a local foobar2000/DeaDBeeF via the beefweb API.</summary>
    public Task LoadBeefweb(int port, string? user, string? pass, bool useSse)
        => SetSource(Beefweb.Watcher.Create(port, user, pass, useSse));

    /// <summary>
    /// Broadcast from a Penumbra mod, given its directory *name*.
    /// </summary>
    public async Task LoadMod(string modDirectoryName)
    {
        var directory = penumbra.ResolveModDirectory(modDirectoryName);
        if (directory is null)
        {
            Plugin.Log.Error(
                $"Could not resolve Penumbra mod '{modDirectoryName}' (Penumbra unavailable or mod missing)");
            return;
        }
        await LoadJukebox(directory);
    }

    private async Task LoadJukebox(string directory)
    {
        Jukebox jukebox;
        try
        {
            jukebox = new Jukebox(player, directory);
            await jukebox.Initialize();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load jukebox");
            return;
        }
        await SetSource(jukebox);
    }

    /// <summary>Swap the active source, disposing the previous one. null stops broadcasting.</summary>
    public async Task SetSource(IMusicSource? source)
    {
        await switchLock.WaitAsync();
        try
        {
            if (disposed)
            {
                if (source is not null) await source.DisposeAsync();
                return;
            }

            IMusicSource? old;
            lock (@lock)
            {
                old = active;
                if (old is not null && activeSourceHandler is not null) old.OnChanged -= activeSourceHandler;
                activeSourceHandler = null;
                active = null;
            }
            if (old is not null) await old.DisposeAsync();

            SourceSnapshot? snap;
            lock (@lock)
            {
                active = source;
                if (source is not null)
                {
                    var src = source;
                    activeSourceHandler = () => OnSourceChanged(src);
                    source.OnChanged += activeSourceHandler;
                }
                cursorEpoch++;
                holdValue = null;
                snap = source?.Current;
            }

            HandleSnapshot(snap);
        }
        finally
        {
            switchLock.Release();
        }
    }

    public (string, string[], PulsarCursor)? CurrentPlayerData()
    {
        lock (@lock) { return UnsafeCompute(); }
    }

    private (string, string[], PulsarCursor)? UnsafeCompute()
    {
        var snap = active?.Current;
        if (snap is null) return null;
        if (prep.TryGet(snap.FilePath, out var result))
        {
            switch (result)
            {
                case PrepResult.Successful s when File.Exists(s.PreparedFilePath):
                    return Map(snap, new PreparedTrack(s.PreparedFilePath, s.GainDb), cursorEpoch);
                case PrepResult.Successful:
                    break;
                case PrepResult.Failed:
                    return null;
            }
        }

        return holdValue;
    }

    /// <summary>
    /// Invoked by a source to report a change... Not when the active source changes.
    /// TODO: make the name better.
    /// </summary>
    private void OnSourceChanged(IMusicSource source)
    {
        SourceSnapshot? snap;
        lock (@lock)
        {
            if (!ReferenceEquals(active, source)) return;
            cursorEpoch++;
            snap = source.Current;
        }
        HandleSnapshot(snap);
    }

    /// <summary>Announce + prep + prefetch for a snapshot, shared by source events and source switches.</summary>
    private void HandleSnapshot(SourceSnapshot? snap)
    {
        AnnounceBroadcasting();
        deliveries.Writer.TryWrite(ArmPrefetchTimer);
        Emit();
        if (snap is not null)
        {
            QueuePrepKick(snap.FilePath);
            if (snap.NextFilePath is { } next)
            {
                Plugin.Log.Debug($"start-prefetch next: {Path.GetFileName(next)}");
                _ = prep.PreparePrefetch(next);
            }
            else Plugin.Log.Debug("start-prefetch: no track available to prefetch");
        }
    }

    private void QueuePrepKick(string originalPath)
    {
        deliveries.Writer.TryWrite(() =>
        {
            lock (@lock)
            {
                if (active?.Current?.FilePath != originalPath) return;
            }
            _ = ReemitWhenPrepped(prep.PrepareActive(originalPath), originalPath);
        });
    }

    private async Task ReemitWhenPrepped(Task<PrepResult> prepTask, string originalPath)
    {
        try
        {
            await prepTask;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "sync-prep failed");
        }

        lock (@lock)
        {
            if (active?.Current?.FilePath != originalPath)
                return;
        }
        Emit();
    }

    private void AnnounceBroadcasting()
    {
        lock (@lock)
        {
            var value = active?.Current is not null;
            if (lastBroadcasting == value) return;
            lastBroadcasting = value;
            deliveries.Writer.TryWrite(() => OnBroadcastingChanged?.Invoke(value));
        }
    }

    private void Emit()
    {
        lock (@lock)
        {
            var data = UnsafeCompute();
            holdValue = data;
            if (EqualsIgnoringPrefetch(data, lastAnnounced)) return;
            lastAnnounced = data;
            deliveries.Writer.TryWrite(() => OnPlayerDataChanged?.Invoke(data));
        }
    }

    /// <summary>
    /// Checks if two Pulsar IPC payloads are equal - ignoring prefetch.
    /// </summary>
    private static bool EqualsIgnoringPrefetch(
        (string, string[], PulsarCursor)? a, (string, string[], PulsarCursor)? b)
    {
        if (a is null || b is null) return a is null && b is null;
        var (fa, _, ca) = a.Value;
        var (fb, _, cb) = b.Value;
        return fa == fb && ca == cb;
    }

    private static (string, string[], PulsarCursor) Map(SourceSnapshot s, PreparedTrack p, int epoch) =>
    (
        p.SyncPath,
        [],
        new PulsarCursor
        {
            PositionMs  = (long)s.Position.TotalMilliseconds,
            IsPlaying   = s.IsPlaying,
            AsOfUnixMs  = s.AsOf.ToUnixTimeMilliseconds(),
            CursorEpoch = epoch,
            Meta        = s.Meta with { ReplayGainDb = p.GainDb },
        }
    );

    private void ArmPrefetchTimer()
    {
        SourceSnapshot? snap;
        lock (@lock) { snap = active?.Current; }

        prefetchFinalPending = false;
        if (snap is null || !snap.IsPlaying) { Plugin.Log.Debug("prefetch timer: disarmed (stopped/paused)"); DisarmTimer(); return; }

        var cfg = config();
        var durMs = snap.Meta.DurationMs;
        if (durMs <= 0) { Plugin.Log.Debug("prefetch timer: disarmed (unknown duration)"); DisarmTimer(); return; }

        var remainingMs = durMs - (long)snap.Position.TotalMilliseconds;
        var untilLead = remainingMs - cfg.PrefetchLeadMs;
        if (untilLead > 0)
        {
            Plugin.Log.Debug($"prefetch timer: lead tick in {untilLead / 1000}s (remaining {remainingMs / 1000}s of {durMs / 1000}s)");
            ArmTimerIn(untilLead);
        }
        else
        {
            prefetchFinalPending = true;   // already inside the lead window
            Plugin.Log.Debug($"prefetch timer: final tick in {Math.Max(0, remainingMs - cfg.PrefetchFinalMs) / 1000}s (remaining {remainingMs / 1000}s, inside {cfg.PrefetchLeadMs / 1000}s lead)");
            ScheduleFinal(remainingMs, cfg);
        }
    }

    private void ScheduleFinal(long remainingMs, Configuration cfg)
    {
        var untilFinal = Math.Max(0, remainingMs - cfg.PrefetchFinalMs);
        ArmTimerIn(untilFinal);
    }

    private void OnPrefetchTick()
    {
        try
        {
            SourceSnapshot? snap;
            lock (@lock) { snap = active?.Current; }
            var next = snap?.NextFilePath;
            Plugin.Log.Debug($"prefetch tick fired: next={(next is null ? "(null)" : Path.GetFileName(next))}");
            if (next != null) prep.PreparePrefetch(next);

            if (!prefetchFinalPending && snap is { Meta.DurationMs: > 0 } s)
            {
                prefetchFinalPending = true;
                var remainingMs = s.Meta.DurationMs - (long)s.Position.TotalMilliseconds;
                ScheduleFinal(remainingMs, config());
            }
        }
        catch (Exception e)
        {
            Plugin.Log.Verbose($"prefetch tick failed: {e.Message}");
        }
    }

    private void ArmTimerIn(long delayMs)
    {
        try { prefetchTimer.Change(TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, int.MaxValue)), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed mid-shutdown */ }
    }

    private void DisarmTimer()
    {
        try { prefetchTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* disposed mid-shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        await prefetchTimer.DisposeAsync();
        await switchLock.WaitAsync();
        try
        {
            disposed = true;
            IMusicSource? a;
            lock (@lock)
            {
                a = active;
                if (a is not null && activeSourceHandler is not null) a.OnChanged -= activeSourceHandler;
                activeSourceHandler = null;
                active = null;
            }
            if (a is not null) await a.DisposeAsync();
        }
        finally
        {
            switchLock.Release();
        }

        deliveries.Writer.TryComplete();
        try
        {
            await deliveryPump.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            Plugin.Log.Warning("broadcast delivery pump did not drain in time");
        }
    }
}
