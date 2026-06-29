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
internal sealed class IpcProvider(IDalamudPluginInterface pi, ListeningManager listening, BroadcastManager broadcast) : IDisposable
{
    private const int MajorVersion = 0;
    private const int MinorVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public bool Enabled { get; private set; }

    private readonly ICallGateProvider<object?> ready = pi.GetIpcProvider<object?>("Pulsar.OnReady");
    private readonly ICallGateProvider<object?> disposing = pi.GetIpcProvider<object?>("Pulsar.OnDisposing");

    private readonly ICallGateProvider<string, string[], string, object?> playerDataChanged =
        pi.GetIpcProvider<string, string[], string, object?>("Pulsar.OnPlayerDataChanged");

    private readonly ICallGateProvider<bool> isEnabled = pi.GetIpcProvider<bool>("Pulsar.IsEnabled");
    private readonly ICallGateProvider<(int, int)> apiVersion = pi.GetIpcProvider<(int, int)>("Pulsar.ApiVersion");

    private readonly ICallGateProvider<(string, string[], string)?> getPlayerData =
        pi.GetIpcProvider<(string, string[], string)?>("Pulsar.GetPlayerData");

    private readonly ICallGateProvider<ulong, string, string[], string, object?> setPlayerData =
        pi.GetIpcProvider<ulong, string, string[], string, object?>("Pulsar.SetPlayerData");
    private readonly ICallGateProvider<ulong, object?> clearPlayerData =
        pi.GetIpcProvider<ulong, object?>("Pulsar.ClearPlayerData");

    public void Prepare()
    {
        if (Enabled) return;

        isEnabled.RegisterFunc(() => Enabled);
        apiVersion.RegisterFunc(() => (MajorVersion, MinorVersion));

        getPlayerData.RegisterFunc(GetPlayerData);

        setPlayerData.RegisterAction(OnSetPlayerData);
        clearPlayerData.RegisterAction(OnClearPlayerData);

        broadcast.OnPlayerDataChanged += OnBroadcastChanged;

        Enabled = true;
    }

    public void NotifyReady()
    {
        if (Enabled) ready.SendMessage();
    }

    private void OnSetPlayerData(ulong ident, string currentFile, string[] prefetchFiles, string cursorJson)
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

    private (string, string[], string)? GetPlayerData()
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

    private void OnBroadcastChanged((string, string[], PulsarCursor)? data)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                if (data is null)
                {
                    playerDataChanged.SendMessage("", [], "");
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

    public void Dispose()
    {
        if (!Enabled) return;

        disposing.SendMessage();

        broadcast.OnPlayerDataChanged -= OnBroadcastChanged;

        isEnabled.UnregisterFunc();
        apiVersion.UnregisterFunc();
        getPlayerData.UnregisterFunc();
        setPlayerData.UnregisterAction();
        clearPlayerData.UnregisterAction();

        Enabled = false;
    }
}
