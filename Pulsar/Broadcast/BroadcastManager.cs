using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Broadcast.Local;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Concurrency;
using Pulsar.Ipc;
using Pulsar.Playback;

namespace Pulsar.Broadcast;

public enum BroadcastMode { Folder, Mod, Beefweb }
public enum BroadcastProvider { Local, Beefweb }

public enum BroadcastPhase { OffAir, Starting, Live, Switching, Retrying, Failed }

public sealed record BroadcastStatusView(
    BroadcastProvider DesiredProvider,
    BroadcastProvider? LiveProvider,
    BroadcastPhase Phase);

public sealed record BrowserLoadView(bool Loading, string? Error);

internal sealed class BroadcastRetryTiming
{
    internal TimeSpan[] Delays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
}

internal abstract record BroadcastOutput
{
    public sealed record PlayerDataChanged : BroadcastOutput, IDisposable
    {
        private List<SyncPrep.ArtifactLease>? artifacts;
        internal BroadcastPlayerData? Data { get; }

        internal PlayerDataChanged(
            BroadcastPlayerData? data,
            List<SyncPrep.ArtifactLease>? artifacts = null)
        {
            Data = data;
            this.artifacts = artifacts;
        }

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref artifacts, null);
            if (owned is null) return;
            foreach (var artifact in owned) artifact.Dispose();
        }
    }
    public sealed record BroadcastingChanged(bool Value) : BroadcastOutput;
}

/// <summary>
/// Owns provider selection, preparation, and broadcast output.
/// Providers publish state; this mailbox decides what goes on air.
/// </summary>
public sealed class BroadcastManager : IAsyncDisposable
{
    private abstract record Message;
    private sealed record IntentChanged(
        BroadcastProvider? Provider,
        bool? OnAir,
        TaskCompletionSource? Completion = null) : Message;
    private sealed record ProviderChanged(ProviderInstance Provider) : Message;
    private sealed record UnsyncableObserved(
        ProviderInstance Provider,
        UnsyncableSource Source) : Message;
    private sealed record ProviderReplaced(
        BroadcastProvider Kind,
        IMusicSource Source,
        TaskCompletionSource Completion) : Message;
    private sealed record PrepCompleted(PreparationAttempt Attempt, PrepResult Result) : Message;
    private sealed record RetryDue(RetrySchedule Schedule) : Message;
    private sealed record RetryRequested : Message;
    private sealed record DisposalCompleted(Task Disposal) : Message;
    private sealed record PrefetchCompleted(
        SourceCursor Cursor,
        string CurrentPath,
        string NextPath,
        PrepResult.Successful Result) : Message;
    private sealed record EngineReconnected : Message;

    private sealed class ProviderInstance(BroadcastProvider kind, IMusicSource source)
    {
        internal BroadcastProvider Kind { get; } = kind;
        internal IMusicSource Source { get; } = source;
        internal Action? ChangedHandler { get; set; }
        internal Action<UnsyncableSource>? UnsyncableHandler { get; set; }
    }

    private sealed record PreparationTarget(
        ProviderInstance Provider,
        SourceCursor Cursor,
        PrepInput Input);

    private sealed class PreparationAttempt(PreparationTarget target)
    {
        internal PreparationTarget Target { get; } = target;
    }

    private sealed class RetrySchedule
    {
        internal CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed record FailureState(
        ProviderInstance Provider,
        PrepInput Input,
        int Failures,
        RetrySchedule? Schedule,
        bool Exhausted);

    private sealed record CommittedBroadcast(
        ProviderInstance Provider,
        SourceCursor Cursor,
        PrepInput Input,
        SourceSnapshot Snapshot,
        PreparedTrack Track,
        SyncPrep.ArtifactLease Artifact,
        BroadcastPlayerData Data);
    private sealed record PreparedPrefetch(
        SourceCursor Cursor,
        string CurrentPath,
        string OriginalPath,
        PreparedTrack Track,
        SyncPrep.ArtifactLease Artifact);
    private sealed record PublishedState(
        SourceSnapshot? Snapshot,
        BroadcastPlayerData? PlayerData,
        BroadcastStatusView Status);

    private readonly IModResolver penumbra;
    private readonly SyncPrep prep;
    private readonly PrefetchScheduler prefetch;
    private readonly EngineSession engineSession;
    private readonly Configuration config;
    private readonly TimeSpan[] retryDelays;
    private readonly TimeSpan providerDisposeTimeout;
    private readonly Lazy<Task> disposeTask;
    private readonly SemaphoreSlim sourceGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SerializedMailbox<Message> mailbox;
    private readonly HashSet<ProviderInstance> providers = [];
    private readonly HashSet<Task> retirements = [];

    private long browserLoadRequest;
    private volatile BrowserLoadView browserLoad = new(false, null);
    private volatile ProviderInstance currentLocal;
    private volatile ProviderInstance? currentBeefweb;
    private BroadcastProvider desiredProvider = BroadcastProvider.Local;
    private bool onAir;
    private PreparationAttempt? activeAttempt;
    private FailureState? failure;
    private CommittedBroadcast? committed;
    private PreparedPrefetch? preparedPrefetch;
    private long cursorEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool lastBroadcasting;
    private BroadcastPlayerData? lastAnnounced;

    private readonly Channel<BroadcastOutput> outputs = Channel.CreateUnbounded<BroadcastOutput>(
        new UnboundedChannelOptions { SingleReader = true });
    internal ChannelReader<BroadcastOutput> Outputs => outputs.Reader;

    private volatile PublishedState published = new(
        null,
        null,
        new BroadcastStatusView(BroadcastProvider.Local, null, BroadcastPhase.OffAir));

    internal BroadcastManager(IRemoteEngine engine, IModResolver penumbra, SyncPrep prep, Configuration config)
        : this(engine, penumbra, prep, config, new PrefetchTiming()) { }

    internal BroadcastManager(
        IRemoteEngine engine,
        IModResolver penumbra,
        SyncPrep prep,
        Configuration config,
        PrefetchTiming prefetchTiming,
        BroadcastRetryTiming? retryTiming = null,
        TimeSpan? providerDisposeTimeout = null)
    {
        this.penumbra = penumbra;
        this.prep = prep;
        this.config = config;
        retryDelays = retryTiming?.Delays ?? new BroadcastRetryTiming().Delays;
        this.providerDisposeTimeout = providerDisposeTimeout ?? TimeSpan.FromSeconds(5);
        engineSession = new EngineSession(engine);
        prefetch = new PrefetchScheduler(prep, prefetchTiming,
            (cursor, current, next, result) =>
                Post(new PrefetchCompleted(cursor, current, next, result)));
        lifetimeToken = lifetimeCts.Token;
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
        currentLocal = Attach(BroadcastProvider.Local, LocalSource.CreateEmpty(engineSession));
        PublishState();
        disposeTask = new Lazy<Task>(FinishDispose, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public LocalSource? ActiveLocalSource => currentLocal.Source as LocalSource;
    public Watcher? ActiveBeefweb => currentBeefweb?.Source as Watcher;
    public bool OnAir => published.Status.Phase != BroadcastPhase.OffAir;
    public BroadcastStatusView BroadcastStatus => published.Status;
    public BrowserLoadView BrowserLoad => browserLoad;
    /// <summary>The snapshot represented by CurrentPlayerData, or null when off air.</summary>
    public SourceSnapshot? CurrentSnapshot => published.Snapshot;

    public void SetProvider(BroadcastProvider provider) => Post(new IntentChanged(provider, null));
    public void SetOnAir(bool value) => Post(new IntentChanged(null, value));
    public void RetryHandoff() => Post(new RetryRequested());
    public void OnEngineReconnected() => Post(new EngineReconnected());

    public async Task LoadFolder(string directory)
    {
        FolderTrackCatalogLoader loader;
        try { loader = new FolderTrackCatalogLoader(directory); }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            FailBrowserLoad(e);
            return;
        }
        await LoadLocalSource(loader, null, null);
    }

    public async Task LoadBeefweb(
        int port,
        string? user,
        string? pass,
        BeefwebTransport transport)
    {
        Watcher watcher;
        try { watcher = Watcher.Create(port, user, pass, transport); }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            return;
        }
        await SetBeefwebSource(watcher);
    }

    internal async Task SetBeefwebSource(Watcher watcher)
    {
        await ReplaceProvider(BroadcastProvider.Beefweb, watcher);
        await ChangeIntent(BroadcastProvider.Beefweb, null);
    }

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
            FailBrowserLoad(e);
            return;
        }
        await LoadLocalSource(loader, modDirectoryName, selectedGroupId);
    }

    internal async Task LoadLocalSource(
        ITrackCatalogLoader loader,
        string? modDirectoryName = null,
        string? selectedGroupId = null)
    {
        var request = Interlocked.Increment(ref browserLoadRequest);
        browserLoad = new BrowserLoadView(true, null);
        try
        {
            await sourceGate.WaitAsync(lifetimeToken);
            try
            {
                var catalog = await loader.LoadAsync(lifetimeToken);
                if (currentLocal.Source is not LocalSource local)
                    throw new InvalidOperationException("The local provider is unavailable");
                await local.ReplaceBrowserAsync(loader, catalog, modDirectoryName, selectedGroupId);
                SetProvider(BroadcastProvider.Local);
                if (request == Volatile.Read(ref browserLoadRequest))
                    browserLoad = new BrowserLoadView(false, null);
            }
            finally { sourceGate.Release(); }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            if (request == Volatile.Read(ref browserLoadRequest))
                browserLoad = new BrowserLoadView(false, null);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to load broadcast source");
            if (request == Volatile.Read(ref browserLoadRequest))
                browserLoad = new BrowserLoadView(false, e.Message);
        }
    }

    internal async Task BroadcastFromForTests(BroadcastProvider kind, IMusicSource source)
    {
        await ReplaceProvider(kind, source);
        await ChangeIntent(kind, true);
    }

    internal Task InstallProviderForTests(BroadcastProvider kind, IMusicSource source)
        => ReplaceProvider(kind, source);

    private async Task ReplaceProvider(BroadcastProvider kind, IMusicSource source)
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(new ProviderReplaced(kind, source, completion)))
        {
            await source.DisposeAsync();
            return;
        }
        await completion.Task;
    }

    private void FailBrowserLoad(Exception error)
    {
        Interlocked.Increment(ref browserLoadRequest);
        browserLoad = new BrowserLoadView(false, error.Message);
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task ChangeIntent(BroadcastProvider? provider, bool? enabled)
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(new IntentChanged(provider, enabled, completion))) return;
        await completion.Task;
    }

    internal BroadcastPlayerData? CurrentPlayerData() => published.PlayerData;
    private void Post(Message message) => mailbox.TryPost(message);

    private ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case IntentChanged(var provider, var enabled, var completion):
                if (provider is { } selected) SelectProvider(selected);
                if (enabled is { } value && onAir != value)
                {
                    onAir = value;
                    ClearFailure();
                }
                Reconcile();
                completion?.TrySetResult();
                break;
            case ProviderChanged(var changed):
                if (!providers.Contains(changed)) break;
                Reconcile();
                break;
            case UnsyncableObserved(var provider, var source):
                if (onAir
                    && config.NotifyUnsyncableBroadcast
                    && desiredProvider == BroadcastProvider.Beefweb
                    && ReferenceEquals(currentBeefweb, provider))
                    NotifyUnsyncable(source);
                break;
            case ProviderReplaced(var kind, var source, var completion):
                try
                {
                    InstallProvider(kind, source);
                    ClearFailure();
                    Reconcile();
                    completion.TrySetResult();
                }
                catch (Exception e)
                {
                    completion.TrySetException(e);
                    throw;
                }
                break;
            case PrepCompleted(var attempt, var result):
                CompletePreparation(attempt, result);
                break;
            case RetryDue(var schedule):
                CompleteRetry(schedule);
                break;
            case RetryRequested:
                if (failure is not { Exhausted: true }) break;
                ClearFailure();
                Reconcile();
                break;
            case DisposalCompleted(var disposal):
                retirements.Remove(disposal);
                break;
            case PrefetchCompleted(var cursor, var currentPath, var nextPath, var result):
                CompletePrefetch(cursor, currentPath, nextPath, result);
                break;
            case EngineReconnected:
                engineSession.OnEngineReconnected();
                break;
        }
        PublishState();
        ReleaseUnusedProviders();
        return ValueTask.CompletedTask;
    }

    private void SelectProvider(BroadcastProvider selected)
    {
        if (desiredProvider == selected) return;

        var previous = desiredProvider;
        desiredProvider = selected;
        ClearFailure();

        // Folder, Mod, and Mod Group share the local queue. Stop its monitor when
        // switching to Beefweb so hidden controls do not keep playing.
        if (previous == BroadcastProvider.Local
            && selected == BroadcastProvider.Beefweb
            && currentLocal.Source is LocalSource local)
            local.Stop();
    }

    private static void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Error(e, "broadcast message failed: {message}", message);
        if (message is IntentChanged(_, _, { } intentCompletion))
            intentCompletion.TrySetException(e);
        if (message is ProviderReplaced(_, _, var completion))
            completion.TrySetException(e);
    }

    private ProviderInstance Attach(BroadcastProvider kind, IMusicSource source)
    {
        // Track the source before subscribing so a failed subscription still disposes it.
        var provider = new ProviderInstance(kind, source);
        providers.Add(provider);
        try
        {
            provider.ChangedHandler = () => Post(new ProviderChanged(provider));
            source.OnChanged += provider.ChangedHandler;
            if (source is Watcher watcher)
            {
                provider.UnsyncableHandler = value => Post(new UnsyncableObserved(provider, value));
                watcher.OnUnsyncable += provider.UnsyncableHandler;
            }
            return provider;
        }
        catch
        {
            Retire(provider);
            throw;
        }
    }

    private void InstallProvider(BroadcastProvider kind, IMusicSource source)
    {
        var old = CurrentProvider(kind);
        var replacement = Attach(kind, source);
        if (kind == BroadcastProvider.Local) currentLocal = replacement;
        else currentBeefweb = replacement;
        if (old is null) return;
        if (ReferenceEquals(activeAttempt?.Target.Provider, old)) activeAttempt = null;
    }

    private ProviderInstance? CurrentProvider(BroadcastProvider kind)
        => kind == BroadcastProvider.Local ? currentLocal : currentBeefweb;
    private ProviderInstance? DesiredProviderInstance() => CurrentProvider(desiredProvider);

    private void Reconcile()
    {
        if (!onAir)
        {
            StopBroadcast();
            return;
        }

        // Keep the current provider live until its replacement is ready.
        if (committed is { } current)
        {
            var liveFrame = current.Provider.Source.Frame;
            if (liveFrame is null)
                StopCommittedProvider();
            else if (ReferenceEquals(liveFrame.Cursor, current.Cursor))
                RefreshProjection(current, liveFrame);
            else
            {
                StartPreparation(current.Provider, liveFrame);
                return;
            }
        }

        var desired = DesiredProviderInstance();
        var desiredFrame = desired?.Source.Frame;
        if (desired is null || desiredFrame is null)
        {
            if (activeAttempt is { } attempt && !IsLiveRefresh(attempt)) activeAttempt = null;
            return;
        }
        if (committed is { } already
            && ReferenceEquals(already.Provider, desired)
            && ReferenceEquals(already.Cursor, desiredFrame.Cursor))
        {
            activeAttempt = null;
            ClearFailureFor(desired, already.Input);
            RefreshProjection(already, desiredFrame);
            return;
        }
        StartPreparation(desired, desiredFrame);
    }

    private void StartPreparation(ProviderInstance provider, SourceFrame frame)
    {
        var liveRefresh = ReferenceEquals(committed?.Provider, provider);
        PrepInput input;
        try { input = PrepInput.Capture(frame.Snapshot.FilePath); }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Cannot inspect preparation input {path}", frame.Snapshot.FilePath);
            activeAttempt = null;
            if (liveRefresh) StopCommittedProvider();
            RecordFailure(provider, new PrepInput(frame.Snapshot.FilePath, 0, 0));
            if (liveRefresh && !ReferenceEquals(DesiredProviderInstance(), provider)) Reconcile();
            return;
        }

        var target = new PreparationTarget(provider, frame.Cursor, input);
        if (activeAttempt is { } running && running.Target == target) return;
        var retrying = failure is { Schedule: null, Exhausted: false } retry
                       && ReferenceEquals(retry.Provider, provider)
                       && retry.Input == input;
        if (!retrying && failure is { } failed
            && ReferenceEquals(failed.Provider, provider)
            && failed.Input == input)
            return;

        if (failure is { } oldFailure
            && (!ReferenceEquals(oldFailure.Provider, provider) || oldFailure.Input != input))
            ClearFailure();

        var attempt = new PreparationAttempt(target);
        activeAttempt = attempt;
        if (TryPrepared(input, out var prepared, out var artifact))
        {
            CommitPrepared(attempt, prepared, artifact);
            return;
        }
        prefetch.Observe(null);
        _ = ReportPreparation(prep.PrepareActive(input), attempt);
    }

    private bool TryPrepared(
        PrepInput input,
        out PreparedTrack prepared,
        out SyncPrep.ArtifactLease artifact)
    {
        if (prep.TryAcquire(input, out var success, out artifact))
        {
            prepared = new PreparedTrack(
                success.PreparedFilePath, success.Blake3Hash, success.Sha1Hash, success.GainDb);
            return true;
        }
        prepared = null!;
        artifact = null!;
        return false;
    }

    private async Task ReportPreparation(Task<PrepResult> task, PreparationAttempt attempt)
    {
        PrepResult result;
        try { result = await task; }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "sync-prep failed");
            result = new PrepResult.Failed(e);
        }
        Post(new PrepCompleted(attempt, result));
    }

    private void CompletePreparation(PreparationAttempt attempt, PrepResult result)
    {
        if (!ReferenceEquals(activeAttempt, attempt)) return;
        var target = attempt.Target;
        var liveRefresh = IsLiveRefresh(attempt);
        var latest = target.Provider.Source.Frame;
        if (latest is null
            || !ReferenceEquals(latest.Cursor, target.Cursor)
            || !target.Input.IsCurrent())
        {
            activeAttempt = null;
            Reconcile();
            return;
        }

        // Refresh the current provider before completing a handoff.
        if (!liveRefresh
            && committed is { } current
            && (current.Provider.Source.Frame is not { } currentLive
                || !ReferenceEquals(currentLive.Cursor, current.Cursor)))
        {
            activeAttempt = null;
            Reconcile();
            return;
        }

        if (!liveRefresh && !ReferenceEquals(DesiredProviderInstance(), target.Provider))
        {
            activeAttempt = null;
            Reconcile();
            return;
        }

        switch (result)
        {
            case PrepResult.Successful success
                when success.Input == target.Input
                     && prep.TryAcquire(target.Input, out var acquired, out var artifact):
                CommitPrepared(attempt, new PreparedTrack(
                    acquired.PreparedFilePath,
                    acquired.Blake3Hash,
                    acquired.Sha1Hash,
                    acquired.GainDb), artifact);
                break;
            case PrepResult.Failed:
            case PrepResult.Successful:
                activeAttempt = null;
                if (liveRefresh) StopCommittedProvider();
                RecordFailure(target.Provider, target.Input);
                Reconcile();
                break;
            case PrepResult.Preempted:
                activeAttempt = null;
                Reconcile();
                break;
        }
    }

    private void CommitPrepared(
        PreparationAttempt attempt,
        PreparedTrack prepared,
        SyncPrep.ArtifactLease artifact)
    {
        if (!TryGetCurrentFrame(attempt, out var latest))
        {
            artifact.Dispose();
            if (ReferenceEquals(activeAttempt, attempt)) activeAttempt = null;
            Reconcile();
            return;
        }
        var old = committed;
        var oldPrefetch = preparedPrefetch;
        activeAttempt = null;
        ClearFailureFor(attempt.Target.Provider, attempt.Target.Input);
        preparedPrefetch = null;
        var epoch = ++cursorEpoch;
        var data = Map(latest.Snapshot, prepared, null, epoch);
        committed = new CommittedBroadcast(
            attempt.Target.Provider,
            attempt.Target.Cursor,
            attempt.Target.Input,
            latest.Snapshot,
            prepared,
            artifact,
            data);
        Emit(data);
        old?.Artifact.Dispose();
        oldPrefetch?.Artifact.Dispose();
        prefetch.Observe(latest);
        Reconcile();
    }

    private bool TryGetCurrentFrame(PreparationAttempt attempt, out SourceFrame latest)
    {
        latest = null!;
        if (!ReferenceEquals(activeAttempt, attempt)) return false;
        var target = attempt.Target;

        var candidateBefore = target.Provider.Source.Frame;
        if (candidateBefore is null
            || !ReferenceEquals(candidateBefore.Cursor, target.Cursor)
            || !target.Input.IsCurrent())
            return false;

        var liveRefresh = IsLiveRefresh(attempt);
        if (!liveRefresh && committed is { } current)
        {
            var live = current.Provider.Source.Frame;
            if (live is null || !ReferenceEquals(live.Cursor, current.Cursor))
                return false;
        }
        if (!liveRefresh && !ReferenceEquals(DesiredProviderInstance(), target.Provider))
            return false;
        latest = candidateBefore;
        return true;
    }

    private bool IsLiveRefresh(PreparationAttempt attempt)
        => ReferenceEquals(committed?.Provider, attempt.Target.Provider);

    private void StopCommittedProvider()
    {
        var old = committed;
        var oldPrefetch = preparedPrefetch;
        activeAttempt = null;
        committed = null;
        preparedPrefetch = null;
        prefetch.Observe(null);
        Emit(null);
        old?.Artifact.Dispose();
        oldPrefetch?.Artifact.Dispose();
    }

    private void StopBroadcast()
    {
        activeAttempt = null;
        ClearFailure();
        StopCommittedProvider();
    }

    private void RefreshProjection(CommittedBroadcast current, SourceFrame latest)
    {
        if (!ReferenceEquals(current.Cursor, latest.Cursor)) return;
        PreparedPrefetch? retiredPrefetch = null;
        if (preparedPrefetch is { } ready
            && (!ReferenceEquals(latest.Cursor, ready.Cursor)
                || latest.Snapshot.FilePath != ready.CurrentPath
                || latest.Snapshot.NextFilePath != ready.OriginalPath))
        {
            retiredPrefetch = ready;
            preparedPrefetch = null;
        }
        var data = Map(
            latest.Snapshot,
            current.Track,
            LivePrefetch(),
            current.Data.Cursor.CursorEpoch);
        committed = current with { Snapshot = latest.Snapshot, Data = data };
        Emit(data);
        retiredPrefetch?.Artifact.Dispose();
        prefetch.Observe(latest);
    }

    private void RecordFailure(ProviderInstance provider, PrepInput input)
    {
        var failures = failure is { } previous
                       && ReferenceEquals(previous.Provider, provider)
                       && previous.Input == input
            ? previous.Failures + 1
            : 1;
        ClearFailure();
        if (failures > retryDelays.Length)
        {
            failure = new FailureState(provider, input, failures, null, Exhausted: true);
            PublishState();
            return;
        }

        var retry = new RetrySchedule();
        failure = new FailureState(provider, input, failures, retry, Exhausted: false);
        _ = PostRetryAfterDelay(retry, retryDelays[failures - 1]);
        PublishState();
    }

    private async Task PostRetryAfterDelay(RetrySchedule retry, TimeSpan delay)
    {
        var token = retry.Cancellation.Token;
        try
        {
            await Task.Delay(delay, token);
            Post(new RetryDue(retry));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void CompleteRetry(RetrySchedule retry)
    {
        if (failure is not { } waiting
            || !ReferenceEquals(waiting.Schedule, retry))
            return;
        retry.Cancellation.Dispose();
        failure = waiting with { Schedule = null };

        if (!onAir
            || (!ReferenceEquals(DesiredProviderInstance(), waiting.Provider)
                && !ReferenceEquals(committed?.Provider, waiting.Provider))
            || waiting.Provider.Source.Frame is not { } frame)
        {
            ClearFailure();
            Reconcile();
            return;
        }

        PrepInput latestInput;
        try { latestInput = PrepInput.Capture(frame.Snapshot.FilePath); }
        catch
        {
            if (waiting.Input.LastWriteTicks == 0
                && waiting.Input.Length == 0
                && waiting.Input.FilePath == frame.Snapshot.FilePath)
            {
                RecordFailure(waiting.Provider, waiting.Input);
                Reconcile();
                return;
            }
            latestInput = default;
        }
        if (latestInput != waiting.Input)
        {
            ClearFailure();
            Reconcile();
            return;
        }
        StartPreparation(waiting.Provider, frame);
    }

    private void ClearFailureFor(ProviderInstance provider, PrepInput input)
    {
        if (failure is { } value
            && ReferenceEquals(value.Provider, provider)
            && value.Input == input)
            ClearFailure();
    }

    private void ClearFailure()
    {
        if (failure is { Schedule: { } retry })
        {
            retry.Cancellation.Cancel();
            retry.Cancellation.Dispose();
        }
        failure = null;
    }

    private void CompletePrefetch(
        SourceCursor cursor,
        string currentPath,
        string nextPath,
        PrepResult.Successful result)
    {
        if (committed is not { } current
            || !ReferenceEquals(current.Cursor, cursor)
            || current.Snapshot.FilePath != currentPath
            || current.Provider.Source.Frame is not { } live
            || !ReferenceEquals(live.Cursor, cursor)
            || live.Snapshot.FilePath != currentPath
            || live.Snapshot.NextFilePath != nextPath
            || result.Input.FilePath != nextPath
            || !result.Input.IsCurrent())
            return;
        if (!prep.TryAcquire(result.Input, out var acquired, out var artifact)) return;
        var oldPrefetch = preparedPrefetch;
        preparedPrefetch = null;
        preparedPrefetch = new PreparedPrefetch(
            cursor,
            currentPath,
            nextPath,
            new PreparedTrack(
                acquired.PreparedFilePath,
                acquired.Blake3Hash,
                acquired.Sha1Hash,
                acquired.GainDb),
            artifact);
        var data = Map(live.Snapshot, current.Track, LivePrefetch(), current.Data.Cursor.CursorEpoch);
        committed = current with { Snapshot = live.Snapshot, Data = data };
        Emit(data);
        oldPrefetch?.Artifact.Dispose();
    }

    private PreparedTrack? LivePrefetch()
        => preparedPrefetch is { Track: var track } && File.Exists(track.SyncPath) ? track : null;

    private static void NotifyUnsyncable(UnsyncableSource source)
    {
        var reason = source.Reason switch
        {
            UnsyncableReason.InternetRadio => "is an internet radio stream",
            UnsyncableReason.CdAudio => "is CD audio",
            UnsyncableReason.Archive => "is inside an archive",
            UnsyncableReason.NotLocalFile => "is not a local file",
            UnsyncableReason.FileDoesNotExist => "failed a file existence check",
            _ => "could not be read",
        };

        if (source.Reason == UnsyncableReason.FileDoesNotExist
            && Dalamud.Utility.Util.IsWine())
            reason = "failed a Linux file existence check; try enabling 'Hack: Force locale to C.utf8' in XIVLauncher if the path contains non-Latin characters";

        ChatNotifier.Warning(
            "Can't broadcast: ",
            $"'{source.Track}' {reason}. We can't sync this. Your listeners won't hear it.");
    }

    private void PublishState()
    {
        var desired = DesiredProviderInstance();
        var desiredFrame = desired?.Source.Frame;
        var live = committed?.Provider;
        var handoffPending = onAir
                             && failure is null
                             && desiredFrame is not null
                             && (live is null || !ReferenceEquals(live, desired)
                                              || !ReferenceEquals(
                                                  committed?.Cursor,
                                                  desiredFrame.Cursor));
        var phase = !onAir ? BroadcastPhase.OffAir
            : failure is { Exhausted: true } ? BroadcastPhase.Failed
            : failure is not null ? BroadcastPhase.Retrying
            : handoffPending && live is not null ? BroadcastPhase.Switching
            : handoffPending ? BroadcastPhase.Starting
            : live is not null ? BroadcastPhase.Live
            : BroadcastPhase.Starting;
        var status = new BroadcastStatusView(desiredProvider, live?.Kind, phase);
        published = new PublishedState(committed?.Snapshot, committed?.Data, status);
    }

    private void Emit(BroadcastPlayerData? data)
    {
        PublishState();
        AnnounceBroadcasting(data is not null);
        if (Equals(data, lastAnnounced)) return;
        List<SyncPrep.ArtifactLease>? artifacts = null;
        if (data is not null)
        {
            artifacts = [];
            if (!prep.TryPinArtifact(data.CurrentPath, out var currentArtifact))
                throw new IOException($"Cannot publish missing prepared artifact: {data.CurrentPath}");
            artifacts.Add(currentArtifact);
            if (!string.IsNullOrEmpty(data.PrefetchPath))
            {
                if (!prep.TryPinArtifact(data.PrefetchPath, out var prefetchArtifact))
                {
                    currentArtifact.Dispose();
                    throw new IOException($"Cannot publish missing prefetch artifact: {data.PrefetchPath}");
                }
                artifacts.Add(prefetchArtifact);
            }
        }
        lastAnnounced = data;
        var publication = new BroadcastOutput.PlayerDataChanged(data, artifacts);
        if (!outputs.Writer.TryWrite(publication)) publication.Dispose();
    }

    private void AnnounceBroadcasting(bool value)
    {
        if (lastBroadcasting == value) return;
        lastBroadcasting = value;
        outputs.Writer.TryWrite(new BroadcastOutput.BroadcastingChanged(value));
    }

    private BroadcastPlayerData Map(
        SourceSnapshot snapshot,
        PreparedTrack current,
        PreparedTrack? prefetch,
        long epoch)
        => new(
            current.SyncPath,
            current.Blake3Hash,
            current.Sha1Hash,
            prefetch?.SyncPath ?? "",
            prefetch?.Blake3Hash ?? "",
            prefetch?.Sha1Hash ?? "",
            new PulsarCursor
            {
                PositionMs = (long)snapshot.Position.TotalMilliseconds,
                IsPlaying = snapshot.IsPlaying,
                AsOfUnixMs = snapshot.AsOf.ToUnixTimeMilliseconds(),
                CursorEpoch = epoch,
                Meta = snapshot.Meta with { ReplayGainDb = current.GainDb },
            });

    private void ReleaseUnusedProviders()
    {
        foreach (var provider in new List<ProviderInstance>(providers))
        {
            if (ReferenceEquals(provider, committed?.Provider)
                || ReferenceEquals(provider, activeAttempt?.Target.Provider)
                || ReferenceEquals(provider, CurrentProvider(provider.Kind)))
                continue;
            Retire(provider);
        }
    }

    private void Retire(ProviderInstance provider)
    {
        if (!providers.Remove(provider)) return;
        if (provider.ChangedHandler is { } changedHandler)
        {
            try { provider.Source.OnChanged -= changedHandler; }
            catch (Exception e)
            {
                Plugin.Log.Verbose($"retired broadcast provider detach failed: {e.Message}");
            }
        }
        if (provider.Source is Watcher watcher
            && provider.UnsyncableHandler is { } unsyncableHandler)
        {
            try { watcher.OnUnsyncable -= unsyncableHandler; }
            catch (Exception e)
            {
                Plugin.Log.Verbose($"retired Beefweb warning detach failed: {e.Message}");
            }
        }
        var disposal = DisposeProvider(provider.Source);
        retirements.Add(disposal);
        _ = ReportDisposal(disposal);
    }

    private async Task ReportDisposal(Task disposal)
    {
        await disposal;
        Post(new DisposalCompleted(disposal));
    }

    private async Task DisposeProvider(IMusicSource source)
    {
        Task disposal;
        try { disposal = source.DisposeAsync().AsTask(); }
        catch (Exception e)
        {
            Plugin.Log.Verbose($"broadcast provider disposal failed: {e.Message}");
            return;
        }
        try { await disposal.WaitAsync(providerDisposeTimeout); }
        catch (TimeoutException)
        {
            Plugin.Log.Warning("Timed out retiring broadcast provider {provider}", source.GetType().Name);
            _ = ObserveLateDisposal(disposal);
        }
        catch (Exception e) { Plugin.Log.Verbose($"retired broadcast provider disposal failed: {e.Message}"); }
    }

    private static async Task ObserveLateDisposal(Task disposal)
    {
        try { await disposal; }
        catch (Exception e)
        {
            Plugin.Log.Verbose($"abandoned broadcast provider disposal failed: {e.Message}");
        }
    }

    private async ValueTask OnCompleted()
    {
        await prefetch.ClearAsync();
        onAir = false;
        StopBroadcast();
        PublishState();
        foreach (var provider in new List<ProviderInstance>(providers)) Retire(provider);
        if (retirements.Count > 0)
            await Task.WhenAll(retirements);
        retirements.Clear();
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
