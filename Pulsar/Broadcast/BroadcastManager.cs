using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Broadcast.Local;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Concurrency;
using Pulsar.Ipc;
using Pulsar.Playback;

namespace Pulsar.Broadcast;

// Which source the broadcast tab plays from. Persisted in Configuration.
public enum BroadcastMode { Folder, Mod, Beefweb }

internal abstract record BroadcastOutput
{
    public sealed record PlayerDataChanged(BroadcastPlayerData? Data) : BroadcastOutput;
    public sealed record BroadcastingChanged(bool Value) : BroadcastOutput;
}

/// <summary>
/// BroadcastManager coordinates the active broadcast source and produces application outputs.
/// It only announces new player data once SyncPrep is finished with transcoding and ReplayGain calculation.
/// Events are input through a mailbox. Outputs leave through an output stream consumed by ApplicationCoordinator.
/// </summary>
public sealed class BroadcastManager : IAsyncDisposable
{
    private abstract record Message;
    private sealed record SourceSet(
        IMusicSource? Source,
        TaskCompletionSource Completion) : Message;
    private sealed record SourceChanged(IMusicSource Source, SourceSnapshot? Snapshot) : Message;
    private sealed record QueueChanged(LocalSource Source) : Message;
    private sealed record PrepCompleted(long Generation, string OriginalPath) : Message;
    private sealed record PrefetchCompleted(
        IMusicSource Source,
        string CurrentPath,
        string NextPath,
        PrepResult.Successful Result) : Message;
    private sealed record EngineReconnected : Message;

    private sealed record PreparedPrefetch(
        string CurrentPath,
        string OriginalPath,
        PreparedTrack Track);

    private sealed record PublishedState(
        IMusicSource? Active,
        SourceSnapshot? Snapshot,
        BroadcastPlayerData? PlayerData);

    private readonly IModResolver penumbra;
    private readonly SyncPrep prep;
    private readonly PrefetchScheduler prefetch;
    private readonly EngineSession engineSession;
    private readonly Configuration config;
    private readonly Lazy<Task> disposeTask;
    // used to serialize source loading (which can be expensive - directory scans and such)
    private readonly SemaphoreSlim sourceGate = new(1, 1);
    private readonly CancellationToken lifetimeToken;

    private readonly SerializedMailbox<Message> mailbox;

    private IMusicSource? active;
    private SourceSnapshot? activeSnapshot;
    private long snapshotGeneration;
    private int cursorEpoch;
    private readonly CancellationTokenSource lifetimeCts = new();
    private bool lastBroadcasting;
    private volatile bool beefwebOnAir;

    private Action<SourceSnapshot?>? activeSourceHandler;
    private Action? activeQueueHandler;

    private BroadcastPlayerData? holdValue; // last computed manifest
    private BroadcastPlayerData? lastAnnounced; // last payload announced
    private PreparedPrefetch? preparedPrefetch;

    private readonly Channel<BroadcastOutput> outputs = Channel.CreateUnbounded<BroadcastOutput>(
        new UnboundedChannelOptions { SingleReader = true });
    internal ChannelReader<BroadcastOutput> Outputs => outputs.Reader;

    private volatile PublishedState published = new(null, null, null);

    public BroadcastManager(IRemoteEngine engine, IModResolver penumbra, SyncPrep prep, Configuration config)
        : this(engine, penumbra, prep, config, new PrefetchTiming()) { }

    internal BroadcastManager(
        IRemoteEngine engine,
        IModResolver penumbra,
        SyncPrep prep,
        Configuration config,
        PrefetchTiming prefetchTiming)
    {
        this.penumbra = penumbra;
        this.prep = prep;
        this.config = config;
        prefetch = new PrefetchScheduler(prep, prefetchTiming,
            (source, current, next, result) =>
                Post(new PrefetchCompleted(source, current, next, result)));
        engineSession = new EngineSession(engine);
        lifetimeToken = lifetimeCts.Token;
        mailbox = new SerializedMailbox<Message>(
            HandleMessage,
            OnMessageError,
            OnCompleted);
        disposeTask = new Lazy<Task>(FinishDispose, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The active source IF they are using the folder or mod player (not beefweb).</summary>
    public LocalSource? ActiveLocalSource => published.Active as LocalSource;

    /// <summary>The active source IF it is the beefweb watcher.</summary>
    public Beefweb.Watcher? ActiveBeefweb => published.Active as Beefweb.Watcher;

    /// <summary>
    /// Whether the Beefweb source should get broadcasted. Not persisted to config,
    /// so it starts as off on every reload, login, etc.
    /// </summary>
    public bool BeefwebOnAir => beefwebOnAir;

    /// <summary>The active source's live snapshot, source-agnostic (for the shared now-playing line).</summary>
    public SourceSnapshot? CurrentSnapshot => published.Snapshot;

    /// <summary>
    /// Reapply volume after reconnect, since a new process will start with volume=1 (max).
    /// </summary>
    public void OnEngineReconnected() => Post(new EngineReconnected());

    /// <summary>Broadcast from a local folder on disk.</summary>
    public async Task LoadFolder(string directory)
    {
        FolderTrackCatalogLoader loader;
        try
        {
            loader = new FolderTrackCatalogLoader(directory);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            return;
        }

        await LoadLocalSource(loader, null, null);
    }

    /// <summary>Broadcast from a local foobar2000/DeaDBeeF via the beefweb API.</summary>
    public async Task LoadBeefweb(int port, string? user, string? pass, bool useSse)
    {
        Beefweb.Watcher watcher;
        try
        {
            watcher = Beefweb.Watcher.Create(port, user, pass, useSse, config);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            return;
        }

        await SetSource(watcher);
        if (ReferenceEquals(ActiveBeefweb, watcher)) watcher.SetOnAir(beefwebOnAir);
    }

    public void SetBeefwebOnAir(bool value)
    {
        beefwebOnAir = value;
        ActiveBeefweb?.SetOnAir(value);
    }

    /// <summary>
    /// Broadcast from a Penumbra mod, given its directory *name*.
    /// </summary>
    public async Task LoadMod(string modDirectoryName, string? selectedGroupId = null)
    {
        ModTrackCatalogLoader loader;
        try
        {
            var directory = penumbra.ResolveModDirectory(modDirectoryName)
                ?? throw new InvalidOperationException(
                    $"Could not resolve Penumbra mod '{modDirectoryName}' "
                    + "(Penumbra unavailable or mod missing)");
            loader = new ModTrackCatalogLoader(directory);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            return;
        }

        await LoadLocalSource(loader, modDirectoryName, selectedGroupId);
    }

    internal async Task LoadLocalSource(
        ITrackCatalogLoader loader,
        string? modDirectoryName = null,
        string? selectedGroupId = null)
    {
        try
        {
            await sourceGate.WaitAsync(lifetimeToken);
            try
            {
                var catalog = await loader.LoadAsync(lifetimeToken);
                var source = await LocalSource.Create(
                    engineSession, loader, catalog, modDirectoryName, selectedGroupId);
                await PostSource(source);
            }
            finally
            {
                sourceGate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested) { }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
        }
    }

    /// <summary>Swap the active source, disposing the previous one. null stops broadcasting.</summary>
    public async Task SetSource(IMusicSource? source)
    {
        try
        {
            await sourceGate.WaitAsync(lifetimeToken);
            try
            {
                await PostSource(source);
            }
            finally
            {
                sourceGate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            if (source is not null) await source.DisposeAsync();
        }
    }

    private async Task PostSource(IMusicSource? source)
    {
        var completion = NewCompletion();
        if (mailbox.TryPost(new SourceSet(source, completion)))
            await completion.Task;
        else if (source is not null)
            await source.DisposeAsync();
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BroadcastPlayerData? CurrentPlayerData() => published.PlayerData;

    private void Post(Message message) => mailbox.TryPost(message);

    private async ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case SourceSet(var source, var completion):
                try
                {
                    await TearDownActive();
                    InstallSource(source);
                    completion.TrySetResult();
                }
                catch
                {
                    if (source is not null && !ReferenceEquals(active, source))
                        await source.DisposeAsync();
                    throw;
                }
                break;

            case SourceChanged(var source, var snapshot):
                if (!ReferenceEquals(active, source)) break;
                cursorEpoch++;
                snapshotGeneration++;
                activeSnapshot = snapshot;
                HandleSnapshot(snapshot);
                break;

            case QueueChanged(var source):
                if (!ReferenceEquals(active, source)) break;
                activeSnapshot = source.Current;
                InvalidatePrefetch(activeSnapshot);
                Emit();
                prefetch.Observe(source, activeSnapshot);
                break;

            case PrepCompleted(var prepGeneration, var originalPath):
                if (prepGeneration == snapshotGeneration
                    && activeSnapshot?.FilePath == originalPath)
                    Emit();
                break;

            case PrefetchCompleted(var source, var currentPath, var nextPath, var result):
                var live = source.Current;
                if (!ReferenceEquals(active, source)
                    || activeSnapshot?.FilePath != currentPath
                    || live?.FilePath != currentPath
                    || live.NextFilePath != nextPath)
                    break;
                preparedPrefetch = new PreparedPrefetch(
                    currentPath,
                    nextPath,
                    new PreparedTrack(
                        result.PreparedFilePath,
                        result.Blake3Hash,
                        result.Sha1Hash,
                        result.GainDb));
                Emit();
                break;

            case EngineReconnected:
                engineSession.OnEngineReconnected();
                break;
        }
    }

    private static void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Error(e, "broadcast message failed: {message}", message);
        switch (message)
        {
            case SourceSet(_, var completion): completion.TrySetException(e); break;
        }
    }

    private void InstallSource(IMusicSource? source)
    {
        active = source;
        if (source is not null)
        {
            var captured = source;
            activeSourceHandler = snapshot => Post(new SourceChanged(captured, snapshot));
            source.OnSnapshotChanged += activeSourceHandler;
            if (source is LocalSource local)
            {
                activeQueueHandler = () => Post(new QueueChanged(local));
                local.OnQueueChanged += activeQueueHandler;
            }
        }

        cursorEpoch++;
        snapshotGeneration++;
        holdValue = null;
        preparedPrefetch = null;
        activeSnapshot = source?.Current;
        HandleSnapshot(activeSnapshot);
    }

    private async Task TearDownActive()
    {
        await prefetch.ClearAsync();
        var old = active;
        if (old is not null && activeSourceHandler is not null)
            old.OnSnapshotChanged -= activeSourceHandler;
        if (old is LocalSource local && activeQueueHandler is not null)
            local.OnQueueChanged -= activeQueueHandler;
        activeSourceHandler = null;
        activeQueueHandler = null;
        active = null;
        activeSnapshot = null;
        preparedPrefetch = null;
        Publish(null);
        if (old is not null) await old.DisposeAsync();
    }

    private BroadcastPlayerData? ComputePlayerData()
    {
        var snap = activeSnapshot;
        if (snap is null) return null;
        if (prep.TryGet(snap.FilePath, out var result))
        {
            switch (result)
            {
                case PrepResult.Successful s when File.Exists(s.PreparedFilePath):
                    return Map(snap,
                        new PreparedTrack(
                            s.PreparedFilePath, s.Blake3Hash, s.Sha1Hash, s.GainDb),
                        LivePrefetch(),
                        cursorEpoch);
                case PrepResult.Successful:
                    break;
                case PrepResult.Failed:
                    return null;
            }
        }

        return holdValue;
    }

    /// <summary>Announce + prep + prefetch for a snapshot, shared by source events and source switches.</summary>
    private void HandleSnapshot(SourceSnapshot? snap)
    {
        InvalidatePrefetch(snap);
        AnnounceBroadcasting();
        Emit();
        if (snap is not null)
            StartActivePrep(snap.FilePath, snapshotGeneration);
        // Active prep is requested first: SyncPrep gives it priority over this best-effort work.
        prefetch.Observe(active, snap);
    }

    private void InvalidatePrefetch(SourceSnapshot? snapshot)
    {
        if (preparedPrefetch is not { } ready) return;
        if (snapshot is not null
            && snapshot.FilePath == ready.CurrentPath
            && snapshot.NextFilePath == ready.OriginalPath)
            return;
        preparedPrefetch = null;
    }

    private PreparedTrack? LivePrefetch()
        => preparedPrefetch is { Track: var track }
           && File.Exists(track.SyncPath)
            ? track
            : null;

    private void StartActivePrep(string originalPath, long generation)
    {
        _ = ReemitWhenPrepped(prep.PrepareActive(originalPath), generation, originalPath);
    }

    private async Task ReemitWhenPrepped(Task<PrepResult> prepTask, long generation, string originalPath)
    {
        try
        {
            await prepTask;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "sync-prep failed");
        }
        Post(new PrepCompleted(generation, originalPath));
    }

    private void AnnounceBroadcasting()
    {
        var value = activeSnapshot is not null;
        if (lastBroadcasting == value) return;
        lastBroadcasting = value;
        outputs.Writer.TryWrite(new BroadcastOutput.BroadcastingChanged(value));
    }

    private void Emit()
    {
        var data = ComputePlayerData();
        holdValue = data;
        Publish(data);
        if (Equals(data, lastAnnounced)) return;
        lastAnnounced = data;
        outputs.Writer.TryWrite(new BroadcastOutput.PlayerDataChanged(data));
    }

    private void Publish(BroadcastPlayerData? data)
        => published = new PublishedState(active, activeSnapshot, data);

    private static BroadcastPlayerData Map(
        SourceSnapshot s,
        PreparedTrack current,
        PreparedTrack? prefetch,
        int epoch) =>
        new(
            current.SyncPath,
            current.Blake3Hash,
            current.Sha1Hash,
            prefetch?.SyncPath ?? "",
            prefetch?.Blake3Hash ?? "",
            prefetch?.Sha1Hash ?? "",
            new PulsarCursor
            {
                PositionMs = (long)s.Position.TotalMilliseconds,
                IsPlaying = s.IsPlaying,
                AsOfUnixMs = s.AsOf.ToUnixTimeMilliseconds(),
                CursorEpoch = epoch,
                Meta = s.Meta with { ReplayGainDb = current.GainDb },
            });

    private async ValueTask OnCompleted()
    {
        await TearDownActive();
        await engineSession.DisposeAsync();
        lifetimeCts.Dispose();
    }

    public ValueTask DisposeAsync() => new(disposeTask.Value);

    private async Task FinishDispose()
    {
        lifetimeCts.Cancel();
        await mailbox.DisposeAsync();
        await prefetch.DisposeAsync();

        outputs.Writer.TryComplete();
    }
}
