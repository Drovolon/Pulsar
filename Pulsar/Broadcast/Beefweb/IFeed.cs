using System;
using System.Collections.Generic;
using System.Threading;

namespace Pulsar.Broadcast.Beefweb;

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
