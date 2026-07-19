namespace Pulsar.Listening;

internal enum NearbyBroadcastContext
{
    Normal,
    AutoPlayOff,
    CurrentlyBroadcasting,
}

internal enum ListeningSilenceReason
{
    MasterMuted,
    PairMuted,
    MasterVolumeZero,
    PairVolumeZero,
}

internal sealed record ListeningTrack(string SourceName, string FilePath, TrackMeta? Meta);

/// <summary>
/// Changes to listening state, emitted by ListeningManager. ApplicationCoordinator routes
/// them to any interested services/modules.
/// </summary>
internal abstract record ListeningOutput
{
    public sealed record ListeningChanged(bool Value) : ListeningOutput;
    public sealed record NearbyBroadcastDetected(
        ListeningTrack Track,
        NearbyBroadcastContext Context) : ListeningOutput;
    public sealed record SilentPlaybackStarted(
        string SourceName,
        ListeningSilenceReason Reason) : ListeningOutput;
    public sealed record TrackChanged(ListeningTrack Track) : ListeningOutput;
}
