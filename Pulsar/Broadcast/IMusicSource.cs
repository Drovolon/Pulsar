
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

public interface IMusicSource : IAsyncDisposable
{
    SourceSnapshot? Current { get; }
    event Action? OnChanged;
}