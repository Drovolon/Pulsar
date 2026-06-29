using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Listening;
using Pulsar.Playback;

namespace Pulsar.Broadcast;

/// <summary>
/// Jukebox thinly wraps DirectoryPlayer, and is the "broadcast songs from a local folder" source.
/// Requires an `await Initialize()` before use, so it can scan the folder.
/// </summary>
public sealed class Jukebox(IRemoteEngine player, string directory) : IMusicSource
{
    /// <summary>The wrapped player, mostly used for UI code.</summary>
    public DirectoryPlayer Player { get; } = new(player, directory);

    public async Task Initialize()
    {
        await Player.Initialize();
        Player.OnChanged += OnPlayerChanged;
    }

    public SourceSnapshot? Current
    {
        get
        {
            var track = Player.CurrentTrack;
            // null means not playing anything. As in, "not broadcasting".
            // This doesn't include paused - paused syncs as PlaybackState.Paused.
            if (Player.State == PlaybackState.Stopped || track is null) return null;

            var pos = Player.Position;
            return new SourceSnapshot(
                track,
                Player.NextTrack,
                Player.State == PlaybackState.Playing,
                pos?.Current ?? TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                new TrackMeta
                {
                    OriginalFileName = Path.GetFileName(track),
                    DurationMs = (long)(pos?.Total.TotalMilliseconds ?? 0),
                });
        }
    }

    public event Action? OnChanged;
    private void OnPlayerChanged() => OnChanged?.Invoke();

    public async ValueTask DisposeAsync()
    {
        Player.OnChanged -= OnPlayerChanged;
        await Player.DisposeAsync();
    }
}
