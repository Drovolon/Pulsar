using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Playback;

public sealed class Jukebox(string directory) : IAsyncDisposable
{
    private string[]? originalFiles;
    private string[]? shuffled;

    private readonly LocalMusicPlayer player = new();
    private readonly Lock @lock = new();

    public int Index { get; private set; } = 0;
    public bool Shuffle { get; private set; } = false;

    public PlaybackPosition? Position => player.Position;
    public bool NowPlaying => player.NowPlaying;

    private static readonly string[] Extensions = ["*.wav", "*.mp3", "*.flac"]; // TODO: refine

    public async Task Initialize()
    {
        originalFiles = await Task.Run(() => Extensions
             .SelectMany(searchPattern =>
                    Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories))
             .OrderBy(f => f)
            .ToArray());
        shuffled = originalFiles;

        player.OnTrackFinished += OnTrackFinished;
    }

    public void ToggleShuffle()
    {
        lock (@lock)
        {
            if (shuffled is null or { Length: 0 }) return;
            var current = shuffled[Index];
            shuffled = Shuffle ? originalFiles! : [ .. shuffled.Shuffle() ];
            Shuffle = !Shuffle;
            Index = Array.IndexOf(shuffled, current);
        }
    }

    private void OnTrackFinished()
    {
        Next();
    }

    public void Stop() => player.Stop();
    public void Volume(float volume) => player.Volume(volume);
    public void Seek(TimeSpan position) => player.Seek(position);

    // The current play order (shuffled or original). Read-only snapshot for the UI.
    public IReadOnlyList<string> Tracks => shuffled ?? [];

    public string? CurrentTrack
    {
        get { lock (@lock) { return shuffled is null or { Length: 0 } ? null : shuffled[Index]; } }
    }

    // Play a specific track from the library list.
    public void PlayIndex(int index)
    {
        lock (@lock)
        {
            if (shuffled is null || index < 0 || index >= shuffled.Length) return;
            Index = index;
            Play();
        }
    }

    public void Play()
    {
        lock (@lock)
        {
            if (shuffled is null or { Length: 0 }) return;
            player.Play(shuffled[Index]);
        }
    }

    public void Next()
    {
        lock (@lock)
        {
            Index++;
            if (Index < shuffled!.Length)
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
        player.OnTrackFinished -= OnTrackFinished;
        await player.DisposeAsync();
    }
}