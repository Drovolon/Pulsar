using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Beefweb.Client;
using Dalamud.Game.Text.SeStringHandling;
using Pulsar.Listening;
using PulsarState = NAudio.Wave.PlaybackState;

namespace Pulsar.Broadcast.Beefweb;

public enum BeefwebTransport { Sse, Polling }

// Disambiguate whether we're connected, disconnected, or unsyncable.
public sealed record BeefwebStatus(bool Connected, UnsyncableSource? Unsyncable);

public sealed record UnsyncableSource(string Track, UnsyncableReason Reason);

/// <summary>
/// The core beefweb logic handler. Takes an IBeefwebFeed, which is a stream of events from Beefweb, and:
/// 1. runs the discriminator to detect "real changes" (not just stuff like changing volume),
/// 2. does disconnection detection,
/// 3. informs the DJ if they try playing a source we don't support syncing (like a web URL),
/// 4. exposes status
/// </summary>
public sealed class Watcher : IMusicSource
{
    private static readonly TimeSpan DefaultGiveUpDelay = TimeSpan.FromSeconds(10);

    private readonly IFeed feed;
    private readonly PlayerClient client;
    private readonly bool isWine;
    private readonly TimeSpan giveUpDelay;
    private readonly Discriminator discriminator = new();
    private readonly Lock @lock = new();
    private readonly CancellationTokenSource cts = new();
    private readonly Timer giveUpTimer;
    private readonly Task pump;

    private Observation? currentObs;
    private SyncVerdict currentVerdict;
    private bool everConnected;
    private bool gaveUp;
    private bool wasUnsyncable;

    // Best-effort "what plays next", resolved off-thread on each track change (slight staleness OK).
    private volatile string? nextLocalPath;

    // giveUpDelay is for tests
    public Watcher(IFeed feed, PlayerClient client, bool isWine, TimeSpan? giveUpDelay = null)
    {
        this.feed = feed;
        this.client = client;
        this.isWine = isWine;
        this.giveUpDelay = giveUpDelay ?? DefaultGiveUpDelay;
        giveUpTimer = new Timer(_ => OnGiveUp(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        feed.OnConnectedChanged += OnConnectedChanged;
        pump = Task.Run(PumpAsync);
    }

    public static Watcher Create(int port, string? user, string? pass, bool useSse)
    {
        var baseUri = new Uri($"http://localhost:{port}");
        var creds = string.IsNullOrEmpty(user) ? null : new ApiCredentials(user, pass ?? "");
        var client = new PlayerClient(baseUri, creds);
        IFeed feed = useSse ? new SseFeed(client) : new PollingFeed(client);
        return new Watcher(feed, client, Dalamud.Utility.Util.IsWine());
    }

    public void TogglePlay()   => Fire(client.PlayOrPause());
    public void StopPlayback() => Fire(client.Stop());
    public void Next()         => Fire(client.PlayNext());
    public void Previous()     => Fire(client.PlayPrevious());

    private static void Fire(ValueTask t) => _ = t.AsTask().ContinueWith(
        x => Plugin.Log.Verbose($"beefweb command failed: {x.Exception?.Message}"),
        TaskContinuationOptions.OnlyOnFaulted);

    public event Action? OnChanged;

    public SourceSnapshot? Current { get { lock (@lock) return BuildSnapshot(); } }

    public BeefwebStatus Status
    {
        get
        {
            lock (@lock)
            {
                var green = feed.Connected || (everConnected && !gaveUp);
                UnsyncableSource? uns = null;
                if (!gaveUp && currentObs is { RawPath: not null, State: not PulsarState.Stopped } o
                    && !currentVerdict.Syncable)
                    uns = new UnsyncableSource(Label(o), currentVerdict.Reason);
                return new BeefwebStatus(green, uns);
            }
        }
    }

    // assumes @lock held
    private SourceSnapshot? BuildSnapshot()
    {
        if (gaveUp || currentObs is not { } o || o.State == PulsarState.Stopped) return null;
        var v = currentVerdict;
        if (!v.Syncable) return null;
        return new SourceSnapshot(
            v.LocalPath!,
            nextLocalPath,
            o.State == PulsarState.Playing,
            o.Position,
            o.AsOf,
            new TrackMeta
            {
                Title = o.Title,
                Artist = o.Artist,
                DurationMs = (long)o.Duration.TotalMilliseconds,
                OriginalFileName = Path.GetFileName(v.LocalPath!),
            });
    }

    // Tries to resolve the next for prefetch. This isn't always possible via the beefweb API sadly.
    private async Task ResolveNextAsync()
    {
        try
        {
            // Check playback queue first
            var queue = await client.GetPlayQueue(PathColumns, cts.Token);
            if (queue.Count > 0)
            {
                nextLocalPath = Syncability.Check(FirstColumn(queue[0].Columns), isWine).LocalPath;
                Plugin.Log.Debug($"Beefweb next: play-queue head ({queue.Count} queued), returning {nextLocalPath ?? "(null)"}");
                return;
            }

            // Otherwise try to use the playlist - but only if shuffle isn't enabled... :(
            var state = await client.GetPlayerState(PathColumns, cts.Token);
            var active = state.ActiveItem;
            if (string.IsNullOrEmpty(active.PlaylistId) || active.Index < 0)
            {
                nextLocalPath = null;
                Plugin.Log.Debug("Beefweb next: no active playlist item, returning null");
                return;
            }
            if (!IsLinearOrder(state))
            {
                nextLocalPath = null;
                Plugin.Log.Debug($"Beefweb next: order unpredictable {DescribeOrder(state)}), returning null");
                return;
            }

            PlaylistRef playlist = active.PlaylistId;
            var items = await client
                            .GetPlaylistItems(playlist, new PlaylistItemRange(active.Index + 1, 1), PathColumns,
                                              cts.Token);
            var raw = items.Items is { Count: > 0 } list ? FirstColumn(list[0].Columns) : null;
            nextLocalPath = Syncability.Check(raw, isWine).LocalPath;
            Plugin.Log.Debug($"Beefweb next: linear ({DescribeOrder(state)}), playlist[{active.Index + 1}] = "
                + $"{(raw is null ? "(end of playlist)" : Path.GetFileName(raw))}, returning {nextLocalPath ?? "(null)"}");
        }
        catch (OperationCanceledException) { /* disposing */ }
        catch (Exception e)
        {
            Plugin.Log.Verbose($"beefweb next-track resolve failed: {e.Message}");
            nextLocalPath = null;
        }
    }

    private static readonly string[] PathColumns = ["%path%"];
    private static string? FirstColumn(IList<string>? cols)
        => cols is { Count: > 0 } ? cols[0] : null;

    // Try to figure out whether we can even reliably predict the next track for prefetch.
    private static bool IsLinearOrder(PlayerState state)
    {
        if (state.Options is not { } options) return false;

        var confirmedOrder = false;
        foreach (var opt in options)
        {
            if (opt.Type != PlayerOptionType.Enum) continue;
            var current = CurrentEnumName(opt);
            if (string.IsNullOrEmpty(current)) continue;
            var c = current.ToLowerInvariant();

            switch (opt.Id.ToLowerInvariant())
            {
                // foobar2000: "Random", "Shuffle (tracks)", "Repeat (track)"
                case "playbackorder":
                case "playbackmode":
                    confirmedOrder = true;
                    if (c.Contains("shuffle") || c.Contains("random") || c.Contains("track")) return false;
                    break;

                // deadbeef shuffle: "Off", "Tracks", "Albums", "Random Tracks"
                case "shuffle":
                    confirmedOrder = true;
                    if (!c.Contains("off")) return false; // anything but off shuffles
                    break;

                // deadbeef repeat: "Off", "One Track", "All Tracks"
                case "repeat":
                    // no point in "prefetching" a single track on repeat
                    if (c.Contains("one") || c.Contains("single")) return false;
                    break;
            }
        }
        // Defensively, only considered linear if we saw an order for sure and identified it was okay
        return confirmedOrder;
    }

    // used for logs
    private static string DescribeOrder(PlayerState state)
    {
        if (state.Options is not { } options) return "no options";
        var s = "";
        foreach (var opt in options)
        {
            if (opt.Type != PlayerOptionType.Enum) continue;
            var id = opt.Id.ToLowerInvariant();
            if (id is "playbackorder" or "playbackmode" or "shuffle" or "repeat")
                s += (s.Length > 0 ? ", " : "") + $"{opt.Id}={CurrentEnumName(opt) ?? "?"}";
        }
        return s.Length > 0 ? s : "no order option";
    }

    // null/type-safe equivalent of opt.EnumNames[opt.Value]
    private static string? CurrentEnumName(PlayerOption opt)
    {
        if (opt.EnumNames is not { } names || opt.Value is not int idx) return null;
        return idx >= 0 && idx < names.Count ? names[idx] : null;
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var obs in feed.Observations(cts.Token))
                await HandleObservation(obs);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Plugin.Log.Error(e, "Beefweb Watcher pump crashed"); }
    }

    private async Task HandleObservation(Observation observation)
    {
        bool triggerEvent;
        bool trackChanged;
        UnsyncableSource? warnUnsyncable = null;
        lock (@lock)
        {
            var wasGiveUp = gaveUp;
            gaveUp = false;
            var ev = discriminator.Classify(observation);
            currentObs = observation;
            triggerEvent = ev is not null || wasGiveUp;
            trackChanged = ev == CursorEvent.TrackChange;

            var loaded = observation is { RawPath: not null, State: not PulsarState.Stopped };
            currentVerdict = loaded ? Syncability.Check(observation.RawPath, isWine) : default;
            var unsyncable = loaded && !currentVerdict.Syncable;
            if (unsyncable && !wasUnsyncable) warnUnsyncable = new UnsyncableSource(Label(observation), currentVerdict.Reason);
            wasUnsyncable = unsyncable;
        }
        
        // Prevent a prefetch resolution race by forcing prefetch resolution here, before we inform upstream
        // TODO: this is sort of a hack, figure out a better architecture
        // Technically, this only affects prefetch-on-load, not the prefetch-near-finish...
        if (trackChanged) await ResolveNextAsync();
        
        if (warnUnsyncable is { } n) NotifyUnsyncable(n, isWine);
        if (triggerEvent) OnChanged?.Invoke();
    }

    private void OnConnectedChanged(bool connected)
    {
        if (connected)
        {
            lock (@lock) everConnected = true;
            giveUpTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        else
        {
            giveUpTimer.Change(giveUpDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnGiveUp()
    {
        bool fire;
        lock (@lock)
        {
            if (feed.Connected) return;
            fire = !gaveUp && currentObs is { State: not PulsarState.Stopped };
            gaveUp = true;
        }
        if (fire) OnChanged?.Invoke();
    }

    private static string Label(Observation o)
        => !string.IsNullOrEmpty(o.Artist) && !string.IsNullOrEmpty(o.Title) ? $"{o.Artist} - {o.Title}"
         : !string.IsNullOrEmpty(o.Title) ? o.Title
         : o.RawPath is { } p ? Path.GetFileName(p) : "(unknown)";

    private static void NotifyUnsyncable(UnsyncableSource u, bool isWine)
    {
        var reason = u.Reason switch
        {
            UnsyncableReason.InternetRadio => "is an internet radio stream",
            UnsyncableReason.CdAudio => "is CD audio",
            UnsyncableReason.Archive => "is inside an archive",
            UnsyncableReason.NotLocalFile => "is not a local file",
            UnsyncableReason.FileDoesNotExist => "had file existence check fail",
            _ => "had an unknown error",
        };

        if (u.Reason == UnsyncableReason.FileDoesNotExist && isWine)
        {
            reason =
                "had file existence check failed on Linux - recommend enabling 'Hack: Force locale to C.utf8' in XIVLauncher settings if this file contains non-Latin characters";
        }
        
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            var se = new SeStringBuilder()
                .AddText("[Pulsar] ")
                .AddUiForeground("Can't broadcast: ", 31).AddUiForegroundOff()
                .AddText($"'{u.Track}' {reason}. We can't sync this. Your listeners won't hear it.");
            Plugin.Chat.Print(se.BuiltString);
        });
    }

    public async ValueTask DisposeAsync()
    {
        feed.OnConnectedChanged -= OnConnectedChanged;
        cts.Cancel();
        await giveUpTimer.DisposeAsync();
        try { await pump; } catch { /* cancellation */ }
        await feed.DisposeAsync();
        client.Dispose();
        cts.Dispose();
    }
}
