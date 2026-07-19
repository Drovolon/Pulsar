using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Pulsar.Api;
using Pulsar.Broadcast;
using Pulsar.Listening;

namespace Pulsar.Ipc;

/// <summary>
/// Our cursor (what is playing, and where we are). Supposed to be opaque to API consumers.
/// </summary>
public sealed record PulsarCursor
{
    public long PositionMs { get; init; }
    public bool IsPlaying { get; init; }
    public long AsOfUnixMs { get; init; }
    public int CursorEpoch { get; init; }
    public TrackMeta? Meta { get; init; }
}

/// <summary>
/// Pulsar's IPC surface. Listener-side functions (SetPlayerData, ClearPlayerData) interact with
/// ListeningManager. Broadcast-side functions (GetPlayerData, OnPlayerDataChanged) interact
/// with BroadcastManager.
/// </summary>
internal sealed class IpcProvider : IDisposable
{
    private const ulong DebugLoopbackIdent = ulong.MaxValue;
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // swapped out for fakes during unit tests
    internal sealed record Gates(
        ICallGateProvider<object?> Ready,
        ICallGateProvider<object?> Disposing,
        ICallGateProvider<PulsarPlayerData?, object?> PlayerDataChanged,
        ICallGateProvider<bool> IsEnabled,
        ICallGateProvider<PulsarApiVersion> ApiVersion,
        ICallGateProvider<PulsarPlayerData?> GetPlayerData,
        ICallGateProvider<ulong, string, string?, string, object?> SetPlayerData,
        ICallGateProvider<ulong, object?> ClearPlayerData)
    {
        public static Gates From(IDalamudPluginInterface pi) => new(
            pi.GetIpcProvider<object?>(PulsarIpcEndpoints.Ready),
            pi.GetIpcProvider<object?>(PulsarIpcEndpoints.Disposing),
            pi.GetIpcProvider<PulsarPlayerData?, object?>(PulsarIpcEndpoints.PlayerDataChanged),
            pi.GetIpcProvider<bool>(PulsarIpcEndpoints.IsEnabled),
            pi.GetIpcProvider<PulsarApiVersion>(PulsarIpcEndpoints.ApiVersion),
            pi.GetIpcProvider<PulsarPlayerData?>(PulsarIpcEndpoints.GetPlayerData),
            pi.GetIpcProvider<ulong, string, string?, string, object?>(PulsarIpcEndpoints.SetPlayerData),
            pi.GetIpcProvider<ulong, object?>(PulsarIpcEndpoints.ClearPlayerData));
    }

    public bool Enabled { get; private set; }

    private readonly ListeningManager listening;
    private readonly BroadcastManager broadcast;

    private readonly ICallGateProvider<object?> ready;
    private readonly ICallGateProvider<object?> disposing;
    private readonly ICallGateProvider<PulsarPlayerData?, object?> playerDataChanged;
    private readonly ICallGateProvider<bool> isEnabled;
    private readonly ICallGateProvider<PulsarApiVersion> apiVersion;
    private readonly ICallGateProvider<PulsarPlayerData?> getPlayerData;
    private readonly ICallGateProvider<ulong, string, string?, string, object?> setPlayerData;
    private readonly ICallGateProvider<ulong, object?> clearPlayerData;

    public IpcProvider(IDalamudPluginInterface pi, ListeningManager listening, BroadcastManager broadcast)
        : this(Gates.From(pi), listening, broadcast) { }

    // for unit tests only
    internal IpcProvider(Gates gates, ListeningManager listening, BroadcastManager broadcast)
    {
        this.listening = listening;
        this.broadcast = broadcast;
        ready = gates.Ready;
        disposing = gates.Disposing;
        playerDataChanged = gates.PlayerDataChanged;
        isEnabled = gates.IsEnabled;
        apiVersion = gates.ApiVersion;
        getPlayerData = gates.GetPlayerData;
        setPlayerData = gates.SetPlayerData;
        clearPlayerData = gates.ClearPlayerData;
    }

    public void Prepare()
    {
        if (Enabled) return;

        isEnabled.RegisterFunc(() => Enabled);
        apiVersion.RegisterFunc(() => PulsarApiVersions.Current);

        getPlayerData.RegisterFunc(GetPlayerData);

        setPlayerData.RegisterAction(OnSetPlayerData);
        clearPlayerData.RegisterAction(OnClearPlayerData);

        Enabled = true;
    }

    public void NotifyReady()
    {
        if (Enabled) ready.SendMessage();
    }

    private void OnSetPlayerData(
        ulong ident, string currentFile, string? prefetchFile, string payload)
    {
        try
        {
            if (string.IsNullOrEmpty(currentFile))
            {
                Plugin.Log.Warning($"SetPlayerData[{ident:X}]: empty current path, ignoring");
                return;
            }

            var cursor = JsonSerializer.Deserialize<PulsarCursor>(payload, JsonOpts);
            if (cursor is null)
            {
                Plugin.Log.Warning($"SetPlayerData[{ident:X}]: empty/invalid cursor, ignoring");
                return;
            }

            Plugin.Log.Debug($"SetPlayerData[{ident:x}] updating to {currentFile}");

            listening.AddOrUpdatePair(ident, ResolveDisplayName(ident), new PairData(
                FilePath: currentFile,
                Position: TimeSpan.FromMilliseconds(cursor.PositionMs),
                IsPlaying: cursor.IsPlaying,
                ObservedAt: DateTimeOffset.FromUnixTimeMilliseconds(cursor.AsOfUnixMs),
                CursorEpoch: cursor.CursorEpoch,
                Meta: cursor.Meta));
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"SetPlayerData[{ident:X}]: failed to apply payload");
        }
    }

    /// <summary> Translates a game address into a character name. </summary>
    private static string? ResolveDisplayName(ulong ident)
    {
        if (ident == ulong.MaxValue) return "Debug Loopback";
        try
        {
            return Plugin.Framework.RunOnFrameworkThread(() =>
            {
                foreach (var obj in Plugin.ObjectTable)
                {
                    if ((ulong)obj.Address != ident) continue;
                    var name = obj.Name.TextValue;
                    return string.IsNullOrEmpty(name) ? null : name;
                }
                return null;
            }).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, $"ResolveDisplayName[{ident:X}] failed; falling back to address");
            return null;
        }
    }

    private void OnClearPlayerData(ulong ident)
    {
        try
        {
            Plugin.Log.Debug($"ClearPlayerData[{ident:x}] clearing");
            listening.ClearPair(ident);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, $"ClearPlayerData[{ident:X}]: failed");
        }
    }

    private PulsarPlayerData? GetPlayerData()
    {
        try
        {
            var data = broadcast.CurrentPlayerData();
            if (data is null) return null;
            Plugin.Log.Debug($"GetPlayerData returning {data.CurrentPath}");
            return ToApiPlayerData(data);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "GetPlayerData failed");
            return null;
        }
    }

    internal void PublishPlayerData(BroadcastPlayerData? data)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                Plugin.Log.Debug($"OnBroadcastChanged triggering with {data?.CurrentPath}");
                if (data is null)
                {
                    playerDataChanged.SendMessage(null);
                    return;
                }
                playerDataChanged.SendMessage(ToApiPlayerData(data));
            }
            catch (Exception e) { Plugin.Log.Error(e, "OnPlayerDataChanged send failed"); }
        });
    }

    private static PulsarPlayerData ToApiPlayerData(BroadcastPlayerData data)
    {
        var current = new PulsarSyncFile(
            data.CurrentPath, data.CurrentBlake3Hash, data.CurrentSha1Hash);
        var prefetch = string.IsNullOrEmpty(data.PrefetchPath)
            ? null
            : new PulsarSyncFile(
                data.PrefetchPath, data.PrefetchBlake3Hash, data.PrefetchSha1Hash);
        return new PulsarPlayerData(
            current, prefetch, JsonSerializer.Serialize(data.Cursor, JsonOpts));
    }

    /// <summary>
    /// Routes local broadcast data through the same JSON and inbound mapping path used by
    /// an external sync plugin, without depending on Dalamud's call-gate registry.
    /// </summary>
    internal void ApplyDebugLoopback(BroadcastPlayerData? data)
    {
        if (data is null)
        {
            OnClearPlayerData(DebugLoopbackIdent);
            return;
        }

        OnSetPlayerData(
            DebugLoopbackIdent,
            data.CurrentPath,
            string.IsNullOrEmpty(data.PrefetchPath) ? null : data.PrefetchPath,
            JsonSerializer.Serialize(data.Cursor, JsonOpts));
    }

    public void Dispose()
    {
        if (!Enabled) return;

        disposing.SendMessage();

        isEnabled.UnregisterFunc();
        apiVersion.UnregisterFunc();
        getPlayerData.UnregisterFunc();
        setPlayerData.UnregisterAction();
        clearPlayerData.UnregisterAction();

        Enabled = false;
    }
}
