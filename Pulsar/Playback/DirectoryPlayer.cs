using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;

namespace Pulsar.Playback;

/// <summary>
/// DirectoryPlayer wraps the FilePlayer. In Initialize(), it enumerates all
/// supported music files in the directory (recursively). Then it offers
/// play/stop/next/prev with shuffle controls. It automatically advances
/// to the next song once the current one finishes.
///
/// DirectoryPlayer doesn't interface with any actual audio API. That's handled
/// by FilePlayer.
/// </summary>
public sealed class DirectoryPlayer : IAsyncDisposable
{
    // the original files, sorted by filename. this is only kept for reference
    // and is never mutated except inside Initialize()
    private string[]? originalFiles;

    // working copy of the files, could be shuffled or original
    private string[]? playlist;

    private readonly Lock @lock = new();

    /// <summary>
    /// Whether the user wants us to be playing music, right now.
    /// </summary>
    private bool playing;
    
    /// <summary>
    /// Number of consecutive load failures.
    /// </summary>
    private int consecutiveFailures;

    /// <summary>
    /// Original directory used for this player.
    /// </summary>
    public string Directory { get; init; }

    /// <summary>
    /// The current play order. Could be shuffled or could be sorted.
    /// </summary>
    public IReadOnlyList<string> Tracks => playlist ?? [];

    /// <summary>
    /// Which track is the current. Starts at index 0.
    /// Next() will advance to index 1. Etc.
    /// </summary>
    public int Index { get; private set; }

    /// <summary>
    /// Whether the file list is currently shuffled.
    /// ToggleShuffle() will flip this on or off, and take care
    /// of updating Tracks.
    /// </summary>
    public bool Shuffle { get; private set; }

    /// <summary>
    /// Where the current track is (how far into the song, and
    /// how long the song is). Null if not playing.
    /// </summary>
    public PlaybackPosition? Position { get; private set; }

    public PlaybackState State { get; private set; }

    /// <summary>
    /// Fired by us on a cursor event (track change / play / pause / resume / seek / stop).
    /// </summary>
    public event Action? OnChanged;

    /// <summary>
    /// Handler fired *by the engine* when something changed: like a playback failure, track ended, etc.
    /// </summary>
    private void OnEngineChanged(object? _, EngineSnapshot snapshot)
    {
        State = snapshot.State;
        Position = snapshot.Position;
        OnChanged?.Invoke();
    }

    // TODO: move somewhere that makes more sense
    private static readonly string[] Extensions = [
        "*.aac", "*.aiff", "*.flac", "*.m4a",
        "*.mp3", "*.ogg", "*.opus", "*.wav",
        "*.wma", "*.wv",
        "*.scd" // hmmm...
    ];

    private readonly CancellationTokenSource asyncCts = new();
    private Task? updateLoop;

    private readonly IRemoteEngine player;
    private readonly EngineQueueManager eqm;

    private readonly TimeSpan pollPlaying;
    private readonly TimeSpan pollIdle;
    private readonly TimeSpan errorBackoff;
    private readonly TimeSpan disposeTimeout;

    public DirectoryPlayer(IRemoteEngine player, string directory)
        : this(player, directory,
               pollPlaying: TimeSpan.FromMilliseconds(250),
               pollIdle: TimeSpan.FromMilliseconds(500),
               errorBackoff: TimeSpan.FromSeconds(1),
               disposeTimeout: TimeSpan.FromSeconds(2)) { }

    // for unit tests only
    internal DirectoryPlayer(IRemoteEngine player, string directory,
        TimeSpan pollPlaying, TimeSpan pollIdle, TimeSpan errorBackoff, TimeSpan disposeTimeout)
    {
        this.player = player;
        Directory = directory;
        this.pollPlaying = pollPlaying;
        this.pollIdle = pollIdle;
        this.errorBackoff = errorBackoff;
        this.disposeTimeout = disposeTimeout;
        eqm = new EngineQueueManager(player, asyncCts.Token);
    }

    /// <summary>
    /// Initializes the DirectoryPlayer by doing a recursive scan.
    /// Async because this can take a while for big directories.
    /// </summary>
    public async Task Initialize()
    {
        originalFiles = await Task.Run(ScanFiles);
        playlist = originalFiles;

        // Go to next once current finishes! Easy.
        // Note: Next wraps around to the playlist beginning once it hits the end.
        // So, this is a repeat-all player, currently.
        player.OnPlaybackEnded += OnEngineEnded;
        player.OnChanged += OnEngineChanged;

        // Deliberately NOT passing the token to Task.Run: a cancel-before-start would
        // put the task in Canceled and make DisposeAsync's await throw. The loop always
        // starts and exits promptly via its own token checks.
        updateLoop = Task.Run(() => UpdateStateLoop(asyncCts.Token));
    }

    // This is purely for Position. Updating state here can lead to some tricky race conditions.
    private async Task UpdateStateLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (State == PlaybackState.Playing)
                {
                    var state = await player.GetStateAsync(ct);
                    Position = state.Position;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "DirectoryPlayer: UpdateStateLoop failed");
                // back off
                try { await Task.Delay(errorBackoff, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }

            try { await Task.Delay(State == PlaybackState.Playing ? pollPlaying : pollIdle, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    // Recursive scan of the directory for supported files, sorted by path in
    // human-friendly numeric order (so "11" precedes "113", not the reverse).
    private string[] ScanFiles() => [
        .. Extensions
           .SelectMany(searchPattern =>
                           System.IO.Directory.EnumerateFiles(Directory, searchPattern, SearchOption.AllDirectories))
           .OrderBy(f => f, NaturalPathComparer.Instance)
    ];

    /// <summary>
    /// Rescan the directory for new files.
    /// </summary>
    public async Task Rescan()
    {
        string[] scanned;
        try
        {
            scanned = await Task.Run(ScanFiles);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "DirectoryPlayer: rescan failed");
            return;
        }

        lock (@lock)
        {
            var current = playlist is null or { Length: 0 } ? null : playlist[Index];
            originalFiles = scanned;
            playlist = Shuffle ? [.. scanned.Shuffle()] : scanned;
            var idx = current is null ? -1 : Array.IndexOf(playlist, current);
            Index = idx >= 0 ? idx : 0;
        }
    }

    public async ValueTask DisposeAsync()
    {
        player.OnPlaybackEnded -= OnEngineEnded;
        player.OnChanged -= OnEngineChanged;
        asyncCts.Cancel();

        try
        {
            await eqm.DisposeAsync().AsTask().WaitAsync(disposeTimeout);
        }
        catch (TimeoutException)
        {
            Plugin.Log.Debug("DirectoryPlayer: EQM drain timed out on dispose");
        }
        
        if (updateLoop is not null)
        {
            try
            {
                await updateLoop.WaitAsync(disposeTimeout);
            }
            catch (TimeoutException)
            {
                Plugin.Log.Debug("DirectoryPlayer: update loop join timed out on dispose");
            }
        }

        // Disposing the player must issue a stop, because nothing else is guaranteed to try.
        try
        {
            using var timeout = new CancellationTokenSource(disposeTimeout);
            await player.StopAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"DirectoryPlayer: stop-on-dispose failed: {e.Message}");
        }
    }

    /// <summary>
    /// We automatically go to the Next() track when the current ends.
    /// But along the way, check if we're having load failures - there's no reason
    /// to burn CPU cycles trying to continually loop an entirely-failing library.
    /// </summary>
    private void OnEngineEnded(object? _, EndReason reason)
    {
        lock (@lock)
        {
            if (!playing) return;

            if (reason == EndReason.Failed)
            {
                consecutiveFailures++;
                var count = playlist?.Length ?? 0;
                if (count == 0 || consecutiveFailures >= count)
                {
                    Plugin.Log.Warning(
                        $"DirectoryPlayer: no playable tracks ({consecutiveFailures} consecutive load failures); stopping.");
                    playing = false;
                    consecutiveFailures = 0;
                    eqm.Stop();
                    return;
                }
            }
            else
            {
                consecutiveFailures = 0;
            }

            UnsafeAdvance();
        }
    }

    public void ToggleShuffle()
    {
        lock (@lock)
        {
            if (playlist is null or { Length: 0 }) return;
            var current = playlist[Index];
            playlist = Shuffle ? originalFiles! : [ .. playlist.Shuffle() ];
            Shuffle = !Shuffle;
            Index = Array.IndexOf(playlist, current);
        }
    }

    public void Stop()
    {
        lock (@lock)
        {
            playing = false;
            consecutiveFailures = 0;
        }

        eqm.Stop();
    }

    // Called after host reset, since the host will come back up with volume=1 (probably)
    public void ReapplyVolume() => eqm.ReapplyVolume();

    public void Pause() => eqm.Pause();
    public void Resume() => eqm.Resume();
    public void Volume(float volume) => eqm.Volume(volume);
    public void Seek(TimeSpan position) => eqm.Seek(position);

    /// <summary>
    /// File name of the currently selected track. Might not necessarily be playing.
    /// </summary>
    public string? CurrentTrack
    {
        get
        {
            lock (@lock)
            {
                return playlist is null or { Length: 0 } ? null : playlist[Index];
            }
        }
    }

    /// <summary>The track that Next()/auto-advance will play.</summary>
    public string? NextTrack
    {
        get
        {
            lock (@lock)
            {
                return playlist is not (null or { Length: 0 }) ? playlist[(Index + 1) % playlist.Length] : null;
            }
        }
    }

    /// <summary>
    /// Play a specific track index. This will begin playing audio.
    /// </summary>
    /// <param name="index"></param>
    public void PlayIndex(int index)
    {
        lock (@lock)
        {
            if (playlist is null || index < 0 || index >= playlist.Length) return;
            Index = index;
            playing = true;
            consecutiveFailures = 0;
            UnsafePlay();
        }
    }

    /// <summary>
    /// Play the currently selected track (whatever Index is).
    /// This will begin to play audio.
    /// </summary>
    public void Play()
    {
        lock (@lock)
        {
            playing = true;
            consecutiveFailures = 0;
            UnsafePlay();
        }
    }

    /// <summary>
    /// Advance to the next track (and play it).
    /// This will wrap back to the beginning of the file list
    /// if it hits the end.
    /// </summary>
    public void Next()
    {
        lock (@lock)
        {
            playing = true;
            consecutiveFailures = 0;
            UnsafeAdvance();
        }
    }

    /// <summary>
    /// Go to the previous track (and play it), if we're less than 5 seconds
    /// into the current song. Otherwise, restart the current song.
    ///
    /// This will restart the first track if it hits the beginning of the library.
    /// </summary>
    public void Prev()
    {
        lock (@lock)
        {
            playing = true;
            consecutiveFailures = 0;
            if (Position?.Current.TotalSeconds <= 5)
            {
                Index--;
                if (Index < 0) Index++;
            }
            UnsafePlay();
        }
    }

    private void UnsafePlay()
    {
        if (playlist is null or { Length: 0 }) return;
        eqm.Load(playlist[Index], TimeSpan.Zero, true);
    }

    private void UnsafeAdvance()
    {
        if (playlist is null or { Length: 0 }) return;
        Index++;
        if (Index >= playlist.Length) Index = 0;
        UnsafePlay();
    }

}
