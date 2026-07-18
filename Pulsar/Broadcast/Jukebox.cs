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
        Player.OnSnapshotChanged += OnPlayerChanged;
    }

    public SourceSnapshot? Current
    {
        get
        {
            var (_, _, _, pos, playbackState, track, nextTrack) = Player.View;
            // null means not playing anything. As in, "not broadcasting".
            // This doesn't include paused - paused syncs as PlaybackState.Paused.
            if (playbackState == PlaybackState.Stopped || track is null) return null;

            return new SourceSnapshot(
                track,
                nextTrack,
                playbackState == PlaybackState.Playing,
                pos?.Current ?? TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                new TrackMeta
                {
                    OriginalFileName = Path.GetFileName(track),
                    DurationMs = (long)(pos?.Total.TotalMilliseconds ?? 0),
                });
        }
    }

    public event Action<SourceSnapshot?>? OnSnapshotChanged;
    private void OnPlayerChanged(EngineSnapshot _) => OnSnapshotChanged?.Invoke(Current);

    public async ValueTask DisposeAsync()
    {
        Player.OnSnapshotChanged -= OnPlayerChanged;
        await Player.DisposeAsync();
    }
}
