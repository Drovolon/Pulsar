using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Playback;

/// <summary>
/// DirectoryPlayer wraps the FilePlayer. In Initialize(), it enumerates all
/// supported music files in the directory (recursively). Then it offers
/// play/stop/next/prev with shuffle controls. It automatically advances
/// to the next song once the current one finishes.
/// </summary>
/// <param name="directory">a directory hopefully containing music files</param>
public sealed class DirectoryPlayer(string directory) : IAsyncDisposable
{
    // the original files, sorted by filename. this is only kept for reference
    // and is never mutated except inside Initialize()
    private string[]? originalFiles;

    // working copy of the files, could be shuffled or original
    private string[]? playlist;

    private readonly FilePlayer player = new();
    private readonly Lock @lock = new();

    /// <summary>
    /// The current play order. Could be shuffled or could be sorted.
    /// </summary>
    public IReadOnlyList<string> Tracks => playlist ?? [];

    /// <summary>
    /// Which track is the current. Starts at index 0.
    /// Next() will advance to index 1. Etc.
    /// </summary>
    public int Index { get; private set; } = 0;

    /// <summary>
    /// Whether or not the file list is currently shuffled.
    /// ToggleShuffle() will flip this on or off, and take care
    /// of updating Tracks.
    /// </summary>
    public bool Shuffle { get; private set; } = false;

    /// <summary>
    /// Where the current track is (how far into the song, and
    /// how long the song is). Null if not playing.
    /// </summary>
    public PlaybackPosition? Position => player.Position;

    /// <summary>
    /// Whether or not something is currently playing.
    /// This really means playing. Like, sound will be playing
    /// from the user's speakers.
    /// </summary>
    public bool NowPlaying => player.NowPlaying;

    private static readonly string[] Extensions = ["*.wav", "*.mp3", "*.flac"]; // TODO: refine

    /// <summary>
    /// Initializes the DirectoryPlayer by doing a recursive scan.
    /// Async because this can take a while for big directories.
    /// </summary>
    public async Task Initialize()
    {
        originalFiles = await Task.Run(() => Extensions
             .SelectMany(searchPattern =>
                    Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories))
             .OrderBy(f => f)
            .ToArray());
        playlist = originalFiles;

        // Go to next once current finishes! Easy.
        // Note: Next wraps around to the playlist beginning once it hits the end.
        // So, this is a repeat-all player, currently.
        player.OnTrackFinished += Next;
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

    public void Stop() => player.Stop();
    public void Volume(float volume) => player.Volume(volume);
    public void Seek(TimeSpan position) => player.Seek(position);

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
            Play();
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
            if (playlist is null or { Length: 0 }) return;
            player.Play(playlist[Index]);
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
            Index++;
            if (Index < playlist!.Length)
            {
                Play();
            }
            else
            {
                Index = 0;
                Play();
            }
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
            if (player.NowPlaying && player.Position?.Current.TotalSeconds <= 5)
            {
                Index--;
                if (Index >= 0)
                {
                    Play();
                }
                else
                {
                    Index++;
                    Play();
                }
            }
            else
            {
                Play();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        player.OnTrackFinished -= Next;
        await player.DisposeAsync();
    }
}