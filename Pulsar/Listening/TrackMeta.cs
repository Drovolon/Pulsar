namespace Pulsar.Listening;

/// <summary>
/// Human-readable metadata for a track, shown in the listener UI.
/// Used in the IPC layer, too. (It's in the sync payload.)
/// </summary>
public sealed record TrackMeta
{
    public string Album { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public long DurationMs { get; init; }
    public string OriginalFileName { get; init; } = "";
    public double ReplayGainDb { get; init; }
}
