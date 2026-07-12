using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace Pulsar.Broadcast.Beefweb;

/// <summary>
/// One sample from beefweb. RawPath is %path% verbatim, which could be a URL.
/// AsOf is wall-clock UtcNow; MonoStamp is a monotonic timestamp (Stopwatch.GetTimestamp()).
/// </summary>
public readonly record struct Observation(
    string? RawPath,
    PlaybackState State,
    TimeSpan Position,
    TimeSpan Duration,
    string Title,
    string Artist,
    DateTimeOffset AsOf,
    long MonoStamp);

/// <summary>
/// Transport that yields beefweb "Observations" and reports its own connection state.
/// Observations() is intended to be an endless stream, until canceled.
/// </summary>
public interface IFeed : IAsyncDisposable
{
    IAsyncEnumerable<Observation> Observations(CancellationToken ct);
    bool Connected { get; }
    event Action<bool>? OnConnectedChanged;
}
