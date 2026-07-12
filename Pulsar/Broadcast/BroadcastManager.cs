using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Ipc;

namespace Pulsar.Broadcast;

// Which source the broadcast tab plays from. Persisted in Configuration.
public enum BroadcastMode { Folder, Mod, Beefweb }

internal abstract record BroadcastOutput
{
    public sealed record PlayerDataChanged((string, string[], PulsarCursor)? Data) : BroadcastOutput;
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
    private sealed record SourceSet(IMusicSource? Source, TaskCompletionSource Completion) : Message;
    private sealed record SourceChanged(IMusicSource Source, SourceSnapshot? Snapshot) : Message;
    private sealed record PrepCompleted(long Generation, string OriginalPath) : Message;
    private sealed record EngineReconnected : Message;
    private sealed record Shutdown(TaskCompletionSource Completion) : Message;

    private sealed record PublishedState(
        IMusicSource? Active,
        SourceSnapshot? Snapshot,
        (string, string[], PulsarCursor)? PlayerData);

    private readonly IModResolver penumbra;
    private readonly SyncPrep prep;
    private readonly PrefetchScheduler prefetch;
    private readonly IRemoteEngine player;

    private readonly Channel<Message> mailbox = Channel.CreateUnbounded<Message>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task messageLoop;

    // Only guards accepting lifecycle requests into the mailbox. Broadcast state itself
    // belongs exclusively to messageLoop.
    private readonly Lock lifecycleLock = new();
    private bool accepting = true;

    private IMusicSource? active;
    private SourceSnapshot? activeSnapshot;
    private long snapshotGeneration;
    private int cursorEpoch;
    private bool lastBroadcasting;

    // The active source's SnapshotChanged subscription, bound to that source's identity so a
    // late event from a torn-down source can be told apart from the live one.
    private Action<SourceSnapshot?>? activeSourceHandler;

    private (string, string[], PulsarCursor)? holdValue;     // last computed manifest; the transcode-gap hold
    private (string, string[], PulsarCursor)? lastAnnounced; // last payload delivered; the dedup comparand

    private readonly Channel<BroadcastOutput> outputs = Channel.CreateUnbounded<BroadcastOutput>(
        new UnboundedChannelOptions { SingleReader = true });
    internal ChannelReader<BroadcastOutput> Outputs => outputs.Reader;

    private volatile PublishedState published = new(null, null, null);

    public BroadcastManager(IRemoteEngine player, IModResolver penumbra, SyncPrep prep, Func<Configuration> config)
    {
        this.penumbra = penumbra;
        this.prep = prep;
        prefetch = new PrefetchScheduler(prep, config);
        this.player = player;
        messageLoop = Task.Run(MessageLoop);
    }

    /// <summary>The active source IF they are using the folder or mod player (not beefweb).</summary>
    public Jukebox? ActiveJukebox => published.Active as Jukebox;

    /// <summary>The active source IF it is the beefweb watcher.</summary>
    public Beefweb.Watcher? ActiveBeefweb => published.Active as Beefweb.Watcher;

    /// <summary>The active source's live snapshot, source-agnostic (for the shared now-playing line).</summary>
    public SourceSnapshot? CurrentSnapshot => published.Snapshot;

    /// <summary>
    /// Reapply volume after reconnect, since a new process will start with volume=1 (max).
    /// </summary>
    public void OnEngineReconnected() => Post(new EngineReconnected());

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
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool accepted;
        lock (lifecycleLock)
        {
            accepted = accepting && mailbox.Writer.TryWrite(new SourceSet(source, completion));
        }

        if (accepted) await completion.Task;
        else if (source is not null) await source.DisposeAsync();
    }

    public (string, string[], PulsarCursor)? CurrentPlayerData() => published.PlayerData;

    private void Post(Message message) => mailbox.Writer.TryWrite(message);

    private async Task MessageLoop()
    {
        await foreach (var message in mailbox.Reader.ReadAllAsync())
        {
            try
            {
                switch (message)
                {
                    case SourceSet(var source, var completion):
                        await SwitchSource(source);
                        completion.TrySetResult();
                        break;

                    case SourceChanged(var source, var snapshot):
                        if (!ReferenceEquals(active, source)) break;
                        cursorEpoch++;
                        snapshotGeneration++;
                        activeSnapshot = snapshot;
                        HandleSnapshot(snapshot);
                        break;

                    case PrepCompleted(var generation, var originalPath):
                        if (generation == snapshotGeneration
                            && activeSnapshot?.FilePath == originalPath)
                            Emit();
                        break;

                    case EngineReconnected:
                        if (active is Jukebox jukebox) jukebox.Player.ReapplyVolume();
                        break;

                    case Shutdown(var completion):
                        await TearDownActive();
                        completion.TrySetResult();
                        return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "broadcast message failed: {message}", message);
                switch (message)
                {
                    case SourceSet(_, var completion): completion.TrySetException(e); break;
                    case Shutdown(var completion): completion.TrySetException(e); return;
                }
            }
        }
    }

    private async Task SwitchSource(IMusicSource? source)
    {
        await TearDownActive();

        active = source;
        if (source is not null)
        {
            var captured = source;
            activeSourceHandler = snapshot => Post(new SourceChanged(captured, snapshot));
            source.SnapshotChanged += activeSourceHandler;
        }

        cursorEpoch++;
        snapshotGeneration++;
        holdValue = null;
        activeSnapshot = source?.Current;
        HandleSnapshot(activeSnapshot);
    }

    private async Task TearDownActive()
    {
        await prefetch.ClearAsync();
        var old = active;
        if (old is not null && activeSourceHandler is not null)
            old.SnapshotChanged -= activeSourceHandler;
        activeSourceHandler = null;
        active = null;
        activeSnapshot = null;
        Publish(null);
        if (old is not null) await old.DisposeAsync();
    }

    private (string, string[], PulsarCursor)? ComputePlayerData()
    {
        var snap = activeSnapshot;
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

    /// <summary>Announce + prep + prefetch for a snapshot, shared by source events and source switches.</summary>
    private void HandleSnapshot(SourceSnapshot? snap)
    {
        AnnounceBroadcasting();
        Emit();
        if (snap is not null)
            StartActivePrep(snap.FilePath, snapshotGeneration);
        // Active prep is requested first: SyncPrep gives it priority over this best-effort work.
        prefetch.Observe(active, snap);
    }

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
        if (EqualsIgnoringPrefetch(data, lastAnnounced)) return;
        lastAnnounced = data;
        outputs.Writer.TryWrite(new BroadcastOutput.PlayerDataChanged(data));
    }

    private void Publish((string, string[], PulsarCursor)? data)
        => published = new PublishedState(active, activeSnapshot, data);

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

    private Task? disposeTask;

    public ValueTask DisposeAsync()
    {
        lock (lifecycleLock)
        {
            if (disposeTask is null)
            {
                accepting = false;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                mailbox.Writer.TryWrite(new Shutdown(completion));
                mailbox.Writer.TryComplete();
                disposeTask = FinishDispose(completion.Task);
            }
            return new ValueTask(disposeTask);
        }
    }

    private async Task FinishDispose(Task shutdown)
    {
        await shutdown;
        await messageLoop;
        await prefetch.DisposeAsync();

        outputs.Writer.TryComplete();
    }
}
