using System;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
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
    private const int MajorVersion = 0;
    private const int MinorVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // swapped out for fakes during unit tests
    internal sealed record Gates(
        ICallGateProvider<object?> Ready,
        ICallGateProvider<object?> Disposing,
        ICallGateProvider<string, string, string, object?> PlayerDataChanged,
        ICallGateProvider<bool> IsEnabled,
        ICallGateProvider<(int, int)> ApiVersion,
        ICallGateProvider<(string, string, string)?> GetPlayerData,
        ICallGateProvider<ulong, string, string, string, object?> SetPlayerData,
        ICallGateProvider<ulong, object?> ClearPlayerData)
    {
        public static Gates From(IDalamudPluginInterface pi) => new(
            pi.GetIpcProvider<object?>("Pulsar.OnReady"),
            pi.GetIpcProvider<object?>("Pulsar.OnDisposing"),
            pi.GetIpcProvider<string, string, string, object?>("Pulsar.OnPlayerDataChanged"),
            pi.GetIpcProvider<bool>("Pulsar.IsEnabled"),
            pi.GetIpcProvider<(int, int)>("Pulsar.ApiVersion"),
            pi.GetIpcProvider<(string, string, string)?>("Pulsar.GetPlayerData"),
            pi.GetIpcProvider<ulong, string, string, string, object?>("Pulsar.SetPlayerData"),
            pi.GetIpcProvider<ulong, object?>("Pulsar.ClearPlayerData"));
    }

    public bool Enabled { get; private set; }

    private readonly ListeningManager listening;
    private readonly BroadcastManager broadcast;

    private readonly ICallGateProvider<object?> ready;
    private readonly ICallGateProvider<object?> disposing;
    private readonly ICallGateProvider<string, string, string, object?> playerDataChanged;
    private readonly ICallGateProvider<bool> isEnabled;
    private readonly ICallGateProvider<(int, int)> apiVersion;
    private readonly ICallGateProvider<(string, string, string)?> getPlayerData;
    private readonly ICallGateProvider<ulong, string, string, string, object?> setPlayerData;
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
        apiVersion.RegisterFunc(() => (MajorVersion, MinorVersion));

        getPlayerData.RegisterFunc(GetPlayerData);

        setPlayerData.RegisterAction(OnSetPlayerData);
        clearPlayerData.RegisterAction(OnClearPlayerData);

        Enabled = true;
    }

    public void NotifyReady()
    {
        if (Enabled) ready.SendMessage();
    }

    private void OnSetPlayerData(ulong ident, string currentFile, string prefetchFile, string cursorJson)
    {
        try
        {
            if (string.IsNullOrEmpty(currentFile))
            {
                listening.ClearPair(ident);
                return;
            }

            var cursor = JsonSerializer.Deserialize<PulsarCursor>(cursorJson, JsonOpts);
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

    private (string, string, string)? GetPlayerData()
    {
        try
        {
            var data = broadcast.CurrentPlayerData();
            if (data is null) return null;
            var (currentFile, prefetch, cursor) = data.Value;
            var cursorJson = JsonSerializer.Serialize(cursor, JsonOpts);
            Plugin.Log.Debug($"GetPlayerData returning {currentFile}");
            return (currentFile, prefetch, cursorJson);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "GetPlayerData failed");
            return null;
        }
    }

    internal void PublishPlayerData((string, string, PulsarCursor)? data)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (data is null)
                {
                    playerDataChanged.SendMessage("", "", "");
                    return;
                }
                var (currentFile, prefetch, cursor) = data.Value;
                var cursorJson = JsonSerializer.Serialize(cursor, JsonOpts);
                Plugin.Log.Debug($"OnBroadcastChanged triggering with {currentFile}");
                playerDataChanged.SendMessage(currentFile, prefetch, cursorJson);
            }
            catch (Exception e) { Plugin.Log.Error(e, "OnPlayerDataChanged send failed"); }
        });
    }

    /// <summary>
    /// Routes local broadcast data through the same JSON and inbound mapping path used by
    /// an external sync plugin, without depending on Dalamud's call-gate registry.
    /// </summary>
    internal void ApplyDebugLoopback((string, string, PulsarCursor)? data)
    {
        if (data is null)
        {
            OnClearPlayerData(DebugLoopbackIdent);
            return;
        }

        var (currentFile, prefetch, cursor) = data.Value;
        OnSetPlayerData(
            DebugLoopbackIdent,
            currentFile,
            prefetch,
            JsonSerializer.Serialize(cursor, JsonOpts));
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
