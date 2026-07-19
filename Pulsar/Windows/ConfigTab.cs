using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Pulsar.Windows;

internal sealed class ConfigTab(Plugin plugin, UiTheme theme)
{
    public void Draw()
    {
        UiUtil.SectionHeader(theme, "VOLUME");
        ImGui.TextDisabled("Mute in-game BGM:");
        DrawCheckbox("While listening to someone else", plugin.Configuration.MuteGameBgmWhileListening,
            value =>
            {
                plugin.Configuration.MuteGameBgmWhileListening = value;
                plugin.RefreshBgmMute();
            });
        DrawCheckbox("While broadcasting", plugin.Configuration.MuteGameBgmWhileBroadcasting,
            value =>
            {
                plugin.Configuration.MuteGameBgmWhileBroadcasting = value;
                plugin.RefreshBgmMute();
            });

        UiUtil.SectionHeader(theme, "NOTIFICATIONS");
        ImGui.TextDisabled("Notify when nearby broadcaster is detected:");
        DrawCheckbox("Normally", plugin.Configuration.NotifyNearbyBroadcaster,
            value => plugin.Configuration.NotifyNearbyBroadcaster = value);
        DrawCheckbox("When Auto-play is Off", plugin.Configuration.NotifyNearbyBroadcasterAutoPlayOff,
            value => plugin.Configuration.NotifyNearbyBroadcasterAutoPlayOff = value);
        DrawCheckbox("While you are broadcasting", plugin.Configuration.NotifyNearbyBroadcasterWhileBroadcasting,
            value => plugin.Configuration.NotifyNearbyBroadcasterWhileBroadcasting = value);

        ImGuiHelpers.ScaledDummy(5f);

        DrawCheckbox("Playback starts while listening volume is muted or zero",
            plugin.Configuration.NotifyMutedPlayback,
            value => plugin.Configuration.NotifyMutedPlayback = value);
        DrawCheckbox("When the song changes",
            plugin.Configuration.NotifyListeningTrackChanged,
            value => plugin.Configuration.NotifyListeningTrackChanged = value);

        ImGuiHelpers.ScaledDummy(5f);

        DrawCheckbox("A Beefweb source cannot be synchronized",
            plugin.Configuration.NotifyUnsyncableBroadcast,
            value => plugin.Configuration.NotifyUnsyncableBroadcast = value);

        UiUtil.SectionHeader(theme, "DEBUG");

        var debug = plugin.Configuration.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debug))
        {
            plugin.Configuration.DebugMode = debug;
            plugin.Configuration.Save();
        }

    }

    private void DrawCheckbox(string label, bool value, System.Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return;
        set(value);
        plugin.Configuration.Save();
    }
}
