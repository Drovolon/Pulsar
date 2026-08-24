
using System;
using Pulsar.Listening;

namespace Pulsar.Broadcast;

public sealed record SourceSnapshot(
    string FilePath,
    string? NextFilePath,
    bool IsPlaying,
    TimeSpan Position,
    DateTimeOffset AsOf,
    TrackMeta Meta);

/// <summary>
/// Identifies one playback state. A source creates a new instance after a load,
/// seek, pause, resume, or start. Queue edits keep the current instance.
/// </summary>
public sealed class SourceCursor
{ }

/// <summary>
/// Groups a playback identity with its snapshot. Events tell consumers to reread it.
/// </summary>
public sealed record SourceFrame(SourceCursor Cursor, SourceSnapshot Snapshot);

public interface IMusicSource : IAsyncDisposable
{
    SourceFrame? Frame { get; }
    event Action? OnChanged;
}
