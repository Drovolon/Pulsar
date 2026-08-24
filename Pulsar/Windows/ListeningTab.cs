using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Pulsar.Listening;

namespace Pulsar.Windows;

internal sealed class ListeningTab(Plugin plugin, UiTheme theme)
{
    private float listenMasterVolume = plugin.Configuration.ListeningMasterVolume;
    private bool listenMasterMuted;

    // Reused across frames to avoid allocations
    private readonly List<CardOption> options = [];
    private readonly List<Action> actions = [];

    public void Draw()
    {
        var listening = plugin.Listening;
        if (listening is null) // this shouldn't happen unless something went horribly wrong during loading
            return;

        var sources = listening.View ?? []; // lock-free snapshot

        PairView? active = null;
        foreach (var p in sources)
            if (p.Active) { active = p; break; }

        UiUtil.SectionHeader(theme, "VOLUME");
        DrawMixer(listening, active);

        UiUtil.SectionHeader(theme, "SOURCE");
        DrawSourceCards(listening, sources);
    }

    private void DrawMixer(ListeningManager listening, PairView? active)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var activeName = active is { } a0 ? (a0.DisplayName ?? $"{a0.Ident:X}") : "Not playing";

        var muteWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var maxCol = avail - (64f * scale) - muteWidth;
        var col = MathF.Max(ImGui.CalcTextSize("Master").X, ImGui.CalcTextSize(activeName).X) + (12f * scale);
        col = MathF.Min(col, MathF.Max(60f * scale, maxCol));

        var master = UiUtil.Fader(theme, "master", "Master", ref listenMasterVolume, listenMasterMuted,
            labelWidth: col);
        if (master.Changed)
            listening.SetMasterVolume(listenMasterVolume);
        if (master.Committed)
        {
            plugin.Configuration.ListeningMasterVolume = listenMasterVolume;
            plugin.Configuration.Save();
        }
        if (master.MuteToggled)
        {
            listenMasterMuted = !listenMasterMuted;
            listening.SetMasterMuted(listenMasterMuted);
        }

        if (active is { } a)
        {
            var vol = a.Volume;
            var r = UiUtil.Fader(theme, "active", activeName, ref vol, a.Muted, labelWidth: col,
                sliderTooltip: $"Volume control just for {a.NameOrFallback}. Stacks with master (ex: 50% master + 50% here == 25% volume).");
            if (r.Changed)
                listening.SetPairVolume(a.Ident, vol);
            if (r.Committed && a.DisplayName is { } persistName)
            {
                plugin.Configuration.ListeningPairVolumes[persistName] = vol;
                plugin.Configuration.Save();
            }
            if (r.MuteToggled)
                listening.SetPairMuted(a.Ident, !a.Muted);
        }
        else
        {
            var zero = 0f;
            UiUtil.Fader(theme, "active", "Not playing", ref zero, muted: false, enabled: false, labelWidth: col);
        }
    }

    private void DrawSourceCards(ListeningManager listening, IReadOnlyList<PairView> sources)
    {
        options.Clear();
        actions.Clear();

        options.Add(new CardOption("Off", "Silence. Ignore nearby broadcasters.", theme.Silent));
        actions.Add(() => SetAmbient(false));

        options.Add(new CardOption("Autoplay nearby", "Play whoever's nearby, one at a time.", theme.Accent));
        actions.Add(() => SetAmbient(true));

        var selected = listening.HasPin ? -1 : (listening.AutoPlay ? 1 : 0);

        foreach (var p in sources)
        {
            options.Add(new CardOption(
                Label: p.NameOrFallback,
                Description: DescribeTrack(p),
                Accent: theme.Accent,
                Trailing: StatusText(p, listening),
                Tooltip: p.Pinned ? null : $"Override Autoplay: play {p.NameOrFallback} or no one (until you re-log).",
                Note: p is { Active: true, Pinned: false } ? "(autoplaying)" : null));
            var ident = p.Ident;
            actions.Add(() => listening.SetActive(ident));
            if (p.Pinned) selected = options.Count - 1;
        }

        if (sources.Count == 0)
        {
            ImGui.TextDisabled("No one nearby is broadcasting.");
            ImGuiHelpers.ScaledDummy(15f);
        }

        var width = ImGui.GetContentRegionAvail().X;
        var clicked = CardGroup.Draw("##sources", System.Runtime.InteropServices.CollectionsMarshal.AsSpan(options), selected, width);
        if (clicked >= 0)
            actions[clicked]();
    }

    private void SetAmbient(bool auto)
    {
        plugin.Listening?.Unpin();
        plugin.Listening?.SetAutoPlay(auto);
        plugin.Configuration.ListeningAutoPlay = auto;
        plugin.Configuration.Save();
    }

    private static string StatusText(in PairView p, ListeningManager listening)
    {
        if (!p.Active)
            return "(in range)";
        var playback = listening.Playback;
        if (playback.Position is { } pos && pos.Total > TimeSpan.Zero
            && playback.Status is ListenerPlaybackStatus.Playing or ListenerPlaybackStatus.Paused)
            return $"{UiUtil.FormatTime(pos.Current)} / {UiUtil.FormatTime(pos.Total)}"
                + (playback.Status == ListenerPlaybackStatus.Paused ? " (paused)" : "");
        return playback.Status switch
        {
            ListenerPlaybackStatus.Loading => "(loading...)",
            ListenerPlaybackStatus.Paused => "(paused)",
            ListenerPlaybackStatus.Ended => "(ended)",
            ListenerPlaybackStatus.Failed => "(failed)",
            _ => "(not playing)",
        };
    }

    /// <summary>
    /// Human-readable description of what's playing.
    /// </summary>
    private static string DescribeTrack(in PairView p)
    {
        if (p.Meta?.DisplayName is { Length: > 0 } displayName)
            return displayName;
        if (p.Meta?.OriginalFileName is { Length: > 0 } name)
            return name;
        return p.FilePath is { } fp ? Path.GetFileName(fp) : "(no track)";
    }
}
