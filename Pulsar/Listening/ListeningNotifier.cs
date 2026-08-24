using System;
using System.IO;

namespace Pulsar.Listening;

/// <summary>
/// Receives listening outputs from ApplicationCoordinator, then (possibly)
/// notifies users things have happened via in-game chat, if enabled in config.
/// </summary>
internal sealed class ListeningNotifier(Configuration config)
{
    internal void Notify(ListeningOutput output)
    {
        switch (output)
        {
            case ListeningOutput.NearbyBroadcastDetected(var track, var context):
                NotifyNearbyBroadcast(track, context);
                break;
            case ListeningOutput.SilentPlaybackStarted(var name, var reason):
                if (config.NotifyMutedPlayback)
                {
                    ChatNotifier.Information("Muted Playback: ", $"{name} started playing, but {SilenceReason(reason)}.");
                }

                break;
            case ListeningOutput.TrackChanged(var track):
                if (config.NotifyListeningTrackChanged)
                {
                    ChatNotifier.Information("Now Playing: ", $"{track.SourceName} is now playing {TrackLabel(track)}.");
                }

                break;
        }
    }

    private void NotifyNearbyBroadcast(ListeningTrack track, NearbyBroadcastContext context)
    {
        var label = TrackLabel(track);
        var nearby = $"{track.SourceName} is playing {label} nearby";
        var (enabled, message) = context switch
        {
            NearbyBroadcastContext.CurrentlyBroadcasting => (config.NotifyNearbyBroadcasterWhileBroadcasting,
                                                                $"{nearby}, but you're currently broadcasting."),
            NearbyBroadcastContext.AutoPlayOff => (config.NotifyNearbyBroadcasterAutoPlayOff,
                                                      $"{nearby}, but Auto-play is Off."),
            NearbyBroadcastContext.Normal => (config.NotifyNearbyBroadcaster, $"{track.SourceName} is playing {label}."),
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };

        if (enabled) ChatNotifier.Information("Nearby Broadcast: ", message);
    }

    private static string TrackLabel(ListeningTrack track)
    {
        if (track.Meta?.DisplayName is { Length: > 0 } displayName) return displayName;
        if (track.Meta?.OriginalFileName is { Length: > 0 } original) return original;
        return Path.GetFileName(track.FilePath);
    }

    private static string SilenceReason(ListeningSilenceReason reason) =>
        reason switch
        {
            ListeningSilenceReason.MasterMuted => "your master listening volume is muted",
            ListeningSilenceReason.PairMuted => "their listening volume is muted",
            ListeningSilenceReason.MasterVolumeZero => "your master listening volume is set to zero",
            ListeningSilenceReason.PairVolumeZero => "their listening volume is set to zero",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
}
