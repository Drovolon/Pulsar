using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Beefweb.Client;
using PulsarState = NAudio.Wave.PlaybackState;

namespace Pulsar.Broadcast.Beefweb;

/// <summary>
/// The world's most overkill solution, this makes sure the columns we send to the API
/// and the way we parse those columns is consistent and maintainable. Adding a new
/// field to be returned is just adding a line to Spec.
/// </summary>
internal static class ActiveItemFields
{
    internal sealed class Fields
    {
        internal string? Path;
        internal string Artist = "";
        internal string Title = "";
    }

    private static readonly (string Query, Action<Fields, string> Assign)[] Spec =
    [
        ("%path%", (f, v) => f.Path = string.IsNullOrEmpty(v) ? null : v),
        ("%artist%", (f, v) => f.Artist = v),
        ("%title%", (f, v) => f.Title = v),
    ];

    internal static string[] Columns { get; } = Spec.Select(s => s.Query).ToArray();

    internal static Fields Parse(IList<string> cols)
    {
        var f = new Fields();
        for (var i = 0; i < Spec.Length && i < cols.Count; i++) Spec[i].Assign(f, cols[i]);
        return f;
    }
}

/// <summary>
/// Polls GET /api/player. Implemented out of caution because I'm not sure how
/// reliable SSE will be. (Particularly on Linux.)
/// </summary>
// pollInterval is for tests
public sealed class PollingFeed(PlayerClient client, TimeSpan? pollInterval = null) : IFeed
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan pollInterval = pollInterval ?? DefaultPollInterval;

    private volatile bool connected;

    public bool Connected => connected;
    public event Action<bool>? OnConnectedChanged;

    public async IAsyncEnumerable<Observation> Observations([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Observation? o = null;
            try
            {
                var state = await client.GetPlayerState(ActiveItemFields.Columns, ct);
                o = BeefwebObservationMapper.Map(state);
                SetConnected(true);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception e)
            {
                SetConnected(false);
                Plugin.Log.Verbose($"beefweb poll failed: {e.Message}");
            }

            if (o is { } val) yield return val;

            try
            {
                await Task.Delay(pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
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

internal static class BeefwebObservationMapper
{
    internal static Observation Map(PlayerState st)
    {
        var state = st.PlaybackState switch
        {
            PlaybackState.Playing => PulsarState.Playing,
            PlaybackState.Paused => PulsarState.Paused,
            _ => PulsarState.Stopped,
        };

        var (f, position, duration) =
            st.PlaybackState != PlaybackState.Stopped && st.ActiveItem.Columns is { Count: > 0 } cols
                ? (ActiveItemFields.Parse(cols), st.ActiveItem.Position, st.ActiveItem.Duration)
                : (new ActiveItemFields.Fields(), TimeSpan.Zero, TimeSpan.Zero);

        return new Observation(f.Path, state, position, duration, f.Title, f.Artist, DateTimeOffset.UtcNow,
                               Stopwatch.GetTimestamp());
    }
}
