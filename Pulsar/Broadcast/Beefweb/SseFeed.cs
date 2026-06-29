using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Beefweb.Client;

namespace Pulsar.Broadcast.Beefweb;

/// <summary>
/// Subscribes to GET /api/query/updates (SSE) via the client's ReadUpdates() stream. Default transport.
/// </summary>
public sealed class SseFeed(PlayerClient client) : IFeed
{
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(2);

    private volatile bool connected;

    public bool Connected => connected;
    public event Action<bool>? OnConnectedChanged;

    public async IAsyncEnumerable<Observation> Observations([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var query = client.CreateQuery().IncludePlayer(ActiveItemFields.Columns);
            var en = query.ReadUpdates().GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool moved;
                    PlayerState? st = null;
                    try
                    {
                        moved = await en.MoveNextAsync();
                        if (moved)
                        {
                            SetConnected(true);
                            st = en.Current.Player;
                        }
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        SetConnected(false);
                        Plugin.Log.Verbose($"beefweb SSE dropped: {e.Message}");
                        break; // fall through to reconnect
                    }

                    if (!moved) break; // stream ended -> try reconnect
                    if (st is not null) yield return BeefwebObservationMapper.Map(st);
                }
            }
            finally { await en.DisposeAsync(); }

            SetConnected(false);
            if (ct.IsCancellationRequested) yield break;
            try { await Task.Delay(Backoff, ct); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private void SetConnected(bool value)
    {
        if (connected == value) return;
        connected = value;
        OnConnectedChanged?.Invoke(value);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
