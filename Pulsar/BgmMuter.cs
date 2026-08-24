using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Config;
using Dalamud.Plugin.Services;

namespace Pulsar;

internal interface IGameBgmControl
{
    bool TryGetMuted(out bool muted);
    void SetMuted(bool muted);
}

internal sealed class GameBgmControl(IGameConfig gameConfig) : IGameBgmControl
{
    public bool TryGetMuted(out bool muted)
    {
        var found = gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint value);
        muted = value != 0;
        return found;
    }

    public void SetMuted(bool muted) => gameConfig.Set(SystemConfigOption.IsSndBgm, muted ? 1u : 0u);
}

/// <summary>
/// Mutes in-game BGM when listening or producing broadcast-side audio, if that is enabled in config.
/// </summary>
internal sealed class BgmMuter(Configuration config, IGameBgmControl gameBgm) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool listening;
    private bool broadcastAudio;
    private bool ownsMute;
    private bool restoreMuted;
    private bool wroteMute;
    private bool disposed;

    internal Task SetListening(bool value) => Update(() => listening = value);
    internal Task SetBroadcastAudio(bool value) => Update(() => broadcastAudio = value);
    internal Task Refresh() => Update(null);

    private async Task Update(Action? change)
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            change?.Invoke();
            await Plugin.Framework.RunOnFrameworkThread(Apply);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Failed to update the in-game BGM mute state");
        } finally
        {
            gate.Release();
        }
    }

    // runs on framework thread
    private void Apply()
    {
        var shouldMute = (listening && config.MuteGameBgmWhileListening) ||
                         (broadcastAudio && config.MuteGameBgmWhileBroadcasting);
        if (shouldMute == ownsMute) return;

        if (shouldMute)
        {
            if (!gameBgm.TryGetMuted(out restoreMuted))
            {
                Plugin.Log.Warning("Could not read the in-game BGM mute state");
                return;
            }

            wroteMute = !restoreMuted;
            if (wroteMute) gameBgm.SetMuted(true);
            ownsMute = true;
            return;
        }

        Restore();
    }

    // runs on framework thread
    private void Restore()
    {
        if (!ownsMute) return;

        if (wroteMute && gameBgm.TryGetMuted(out var current) && current)
            gameBgm.SetMuted(restoreMuted);

        ownsMute = false;
        wroteMute = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            try
            {
                await Plugin.Framework.RunOnFrameworkThread(Restore);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Failed to restore the in-game BGM mute state");
            }
        } finally
        {
            gate.Release();
        }
    }
}
