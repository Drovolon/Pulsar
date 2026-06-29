using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Playback;

namespace Pulsar.Windows;

internal sealed class BroadcastTab(Plugin plugin, FileDialogManager fileDialogManager, UiTheme theme)
{
    private static readonly string[] ModeLabels =
        ["Play a folder", "Play files from a Penumbra mod", "Local player (Foobar2000 or DeaDBeeF via beefweb API)"];

    private BroadcastMode mode = plugin.Configuration.BroadcastMode;

    private float volume = plugin.Configuration.MonitorVolume;
    private bool broadcastMuted;
    private bool seeking;
    private float? pendingSeek;
    private float seekValue;
    private DirectoryPlayer? lastPlayer;
    private int lastScrolledIndex = -1;

    // search filter. note: doesn't affect playlist, just the results table in UI
    private string libFilter = "";
    private IReadOnlyList<string>? lastTracks;
    private string lastLibFilter = "";
    // Name = filename; Dir = folder relative to the player root ("" for root-level
    // files); Rel = the two joined, used for filtering and the hover tooltip.
    private readonly List<(int Index, string Name, string Dir, string Rel)> filteredTracks = [];

    private IReadOnlyList<ModEntry>? mods;
    private readonly FilterCombo<ModEntry> modCombo = new(m => m.Name);

    public void Draw()
    {
        UiUtil.SectionHeader(theme, "SOURCE");

        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##source", ModeLabels[(int)mode]))
        {
            for (var i = 0; i < ModeLabels.Length; i++)
            {
                var selected = i == (int)mode;
                if (ImGui.Selectable(ModeLabels[i], selected) && !selected)
                {
                    mode = (BroadcastMode)i;
                    plugin.Configuration.BroadcastMode = mode;
                    plugin.Configuration.Save();
                    OnModeChanged();
                }
            }
            ImGui.EndCombo();
        }

        switch (mode)
        {
            case BroadcastMode.Folder:
                DrawFolder();
                break;
            case BroadcastMode.Mod:
                DrawMod();
                break;
            case BroadcastMode.Beefweb:
                DrawBeefweb();
                break;
        }
    }

    private void OnModeChanged()
    {
        switch (mode)
        {
            case BroadcastMode.Folder:
                var folders = plugin.Configuration.BroadcastFolders;
                _ = folders.Count > 0 ? plugin.Broadcast?.LoadFolder(folders[0]) : plugin.Broadcast?.SetSource(null);
                break;
            case BroadcastMode.Mod:
                if (plugin.Configuration.BroadcastMod is { } mod) _ = plugin.Broadcast?.LoadMod(mod);
                else _ = plugin.Broadcast?.SetSource(null);
                break;
            case BroadcastMode.Beefweb:
                ReloadBeefweb();
                break;
        }
    }

    private void DrawFolder()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, "Select Folder"))
        {
            fileDialogManager.OpenFolderDialog("Select a music folder", (success, dir) =>
            {
                if (!success) return;
                plugin.Configuration.BroadcastFolders = [dir];
                plugin.Configuration.Save();
                _ = plugin.Broadcast?.LoadFolder(dir);
            });
        }
        ImGui.SameLine();
        if (UiUtil.IconButton("folderrefresh", FontAwesomeIcon.Sync, "Refresh"))
        {
            if (plugin.Broadcast?.ActiveJukebox?.Player is { } p) _ = p.Rescan();
        }

        var savedFolder = plugin.Configuration.BroadcastFolders.Count > 0
            ? plugin.Configuration.BroadcastFolders[0]
            : null;
        ImGui.SameLine();
        ImGui.TextDisabled(savedFolder ?? "(no folder)");

        DrawPlayer();
    }

    private void DrawMod()
    {
        if (plugin.Penumbra?.IsAvailable is false or null)
        {
            ImGui.TextDisabled("Penumbra isn't installed or hasn't finished loading.");
            mods = null;
            return;
        }

        mods ??= plugin.Penumbra.GetMods();

        var selectedDir = plugin.Configuration.BroadcastMod;
        var current = selectedDir is null ? null : mods.FirstOrDefault(m => m.DirectoryName == selectedDir);
        var preview = current?.Name ?? selectedDir ?? "(no mod selected)";

        if (UiUtil.IconButton("modrefresh", FontAwesomeIcon.Sync, "Refresh"))
        {
            mods = plugin.Penumbra.GetMods();
            if (plugin.Broadcast?.ActiveJukebox?.Player is { } p) _ = p.Rescan();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (modCombo.Draw("##mod", preview, mods, current, out var picked))
        {
            plugin.Configuration.BroadcastMod = picked!.DirectoryName;
            plugin.Configuration.Save();
            _ = plugin.Broadcast?.LoadMod(picked.DirectoryName);
        }

        DrawPlayer();
    }

    private void ReloadBeefweb()
    {
        var c = plugin.Configuration;
        _ = plugin.Broadcast?.LoadBeefweb(c.BeefwebPort, c.BeefwebUsername, c.BeefwebPassword,
            c.BeefwebTransport == BeefwebTransport.Sse);
    }

    private void DrawBeefweb()
    {
        DrawBeefwebConfig();
        DrawBeefwebControls();
    }

    private void DrawBeefwebConfig()
    {
        UiUtil.SectionHeader(theme, "CONFIG");

        var c = plugin.Configuration;
        var status = plugin.Broadcast?.ActiveBeefweb?.Status;
        var connected = status?.Connected ?? false;

        var col = connected ? theme.Accent : new Vector4(0.85f, 0.25f, 0.25f, 1f);
        UiUtil.IconColored(FontAwesomeIcon.Circle, col);
        ImGui.SameLine();
        if (connected)
        {
            ImGui.TextUnformatted($"Connected to localhost:{c.BeefwebPort}");
        }
        else
        {
            ImGui.TextUnformatted("Not detected.");
            ImGui.TextDisabled($"Is foobar2000/DeaDBeeF running on localhost:{c.BeefwebPort}?");
            ImGui.TextDisabled($"(Reminder: beefweb component must be installed. See setup guide.)");
        }

        var port = c.BeefwebPort;
        ImGui.SetNextItemWidth(50f * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Port", ref port))
            c.BeefwebPort = Math.Clamp(port, 1, 65535);
        if (ImGui.IsItemDeactivatedAfterEdit()) { c.Save(); ReloadBeefweb(); }

        if (ImGui.TreeNode("Advanced"))
        {
            var user = c.BeefwebUsername ?? "";
            var pass = c.BeefwebPassword ?? "";
            var reload = false;
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("Username", ref user, 128)) c.BeefwebUsername = user.Length == 0 ? null : user;
            if (ImGui.IsItemDeactivatedAfterEdit()) reload = true;
            ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputText("Password", ref pass, 128, ImGuiInputTextFlags.Password))
                c.BeefwebPassword = pass.Length == 0 ? null : pass;
            if (ImGui.IsItemDeactivatedAfterEdit()) reload = true;

            var sse = c.BeefwebTransport == BeefwebTransport.Sse;
            if (ImGui.Checkbox("Use SSE (recommended)", ref sse))
            {
                c.BeefwebTransport = sse ? BeefwebTransport.Sse : BeefwebTransport.Polling;
                reload = true;
            }
            if (reload) { c.Save(); ReloadBeefweb(); }
            ImGui.TreePop();
        }
    }

    private void DrawBeefwebControls()
    {
        if (plugin.Broadcast?.ActiveBeefweb?.Status.Connected == false) return;

        UiUtil.SectionHeader(theme, "CONTROLS");

        var snap = plugin.Broadcast?.CurrentSnapshot;
        var uns = plugin.Broadcast?.ActiveBeefweb?.Status.Unsyncable;
        if (uns != null)
        {
            var reason = uns.Reason switch
            {
                UnsyncableReason.InternetRadio => "internet radio",
                UnsyncableReason.CdAudio => "CD audio",
                UnsyncableReason.Archive => "inside an archive",
                _ => "not a local file",
            };
            UiUtil.NowPlaying(theme, FontAwesomeIcon.ExclamationTriangle, $"Can't sync '{uns.Track}' ({reason})");
        }
        else
        {
            DrawNowPlaying(snap);
        }
        ImGuiHelpers.ScaledDummy(2f);

        var connected = plugin.Broadcast?.ActiveBeefweb?.Status.Connected ?? false;
        using (ImRaii.Disabled(!connected))
        {
            var playing = snap is { IsPlaying: true };
            if (UiUtil.IconButton("bwplay", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                    playing ? "Pause" : "Play"))
                plugin.Broadcast?.ActiveBeefweb?.TogglePlay();
            ImGui.SameLine();
            if (UiUtil.IconButton("bwstop", FontAwesomeIcon.Stop, "Stop")) plugin.Broadcast?.ActiveBeefweb?.StopPlayback();
            ImGui.SameLine();
            if (UiUtil.IconButton("bwprev", FontAwesomeIcon.Backward, "Previous")) plugin.Broadcast?.ActiveBeefweb?.Previous();
            ImGui.SameLine();
            if (UiUtil.IconButton("bwnext", FontAwesomeIcon.Forward, "Next")) plugin.Broadcast?.ActiveBeefweb?.Next();
        }
    }

    private void DrawNowPlaying(SourceSnapshot? snap)
    {
        if (snap is null)
        {
            UiUtil.NowPlaying(theme, FontAwesomeIcon.Music, "Not playing");
            return;
        }

        var label = !string.IsNullOrEmpty(snap.Meta.Artist) && !string.IsNullOrEmpty(snap.Meta.Title)
            ? $"{snap.Meta.Artist} - {snap.Meta.Title}"
            : !string.IsNullOrEmpty(snap.Meta.Title) ? snap.Meta.Title
            : Path.GetFileName(snap.FilePath);
        UiUtil.NowPlaying(theme, FontAwesomeIcon.Music, $"{(snap.IsPlaying ? "Playing" : "Paused")}: {label}");

        var total = TimeSpan.FromMilliseconds(snap.Meta.DurationMs);
        if (total > TimeSpan.Zero)
        {
            var elapsed = snap.Position + (snap.IsPlaying ? DateTimeOffset.UtcNow - snap.AsOf : TimeSpan.Zero);
            if (elapsed > total) elapsed = total;
            UiUtil.ProgressMeter(theme, (float)(elapsed / total), snap.IsPlaying,
                $"{UiUtil.FormatTime(elapsed)} / {UiUtil.FormatTime(total)}");
        }
    }

    // Shared transport + library controls for whatever Jukebox is currently active (folder or mod).
    private void DrawPlayer()
    {
        var player = plugin.Broadcast?.ActiveJukebox?.Player;

        if (player is not null && !ReferenceEquals(player, lastPlayer))
        {
            player.Volume(broadcastMuted ? 0f : volume);
            if (player.Shuffle != plugin.Configuration.Shuffle) player.ToggleShuffle();
            lastPlayer = player;
        }

        if (player is null)
        {
            ImGui.TextDisabled("Nothing loaded.");
            return;
        }

        UiUtil.SectionHeader(theme, "PLAYER");

        var track = player.CurrentTrack;
        var status = player.State switch
        {
            PlaybackState.Playing when track is not null => $"Playing: {Path.GetFileName(track)}",
            PlaybackState.Paused when track is not null => $"Paused: {Path.GetFileName(track)}",
            _ => "Not playing"
        };
        UiUtil.NowPlaying(theme, FontAwesomeIcon.Music, status);
        ImGuiHelpers.ScaledDummy(2f);

        DrawControls(player);
        DrawSeek(player);

        UiUtil.SectionHeader(theme, "LIBRARY");
        DrawLibrary(player);
    }

    private void DrawControls(DirectoryPlayer player)
    {
        var playing = player.State == PlaybackState.Playing;
        if (UiUtil.IconButton("playpause", playing ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play,
                playing ? "Pause" : player.State == PlaybackState.Paused ? "Resume" : "Play"))
        {
            if (playing) player.Pause();
            else if (player.State == PlaybackState.Paused) player.Resume();
            else player.Play();
        }
        ImGui.SameLine();
        if (UiUtil.IconButton("stop", FontAwesomeIcon.Stop, "Stop")) player.Stop();
        ImGui.SameLine();
        if (UiUtil.IconButton("prev", FontAwesomeIcon.Backward, "Previous")) player.Prev();
        ImGui.SameLine();
        if (UiUtil.IconButton("next", FontAwesomeIcon.Forward, "Next")) player.Next();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Random, player.Shuffle ? "On" : "Off"))
        {
            player.ToggleShuffle();
            plugin.Configuration.Shuffle = player.Shuffle;
            plugin.Configuration.Save();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shuffle");

        ImGui.SameLine();
        var frameH = ImGui.GetFrameHeight();
        var muteWidth = frameH + ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var sliderW = MathF.Max(60f * ImGuiHelpers.GlobalScale, avail - muteWidth);
        ImGui.SetNextItemWidth(sliderW);
        var sliderPos = UiUtil.AmplitudeToSlider(volume);
        var changed = ImGui.SliderFloat("##bcastvol", ref sliderPos, 0f, 1f, $"{sliderPos * 100f:0}%%");
        if (changed) volume = UiUtil.SliderToAmplitude(sliderPos);
        var committed = ImGui.IsItemDeactivatedAfterEdit();
        ImGui.SameLine();
        var muteToggled = UiUtil.IconButton("bcastmute",
            broadcastMuted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
            broadcastMuted ? "Unmute" : "Mute");

        if (changed) player.Volume(broadcastMuted ? 0f : volume);
        if (committed)
        {
            plugin.Configuration.MonitorVolume = volume;
            plugin.Configuration.Save();
        }
        if (muteToggled)
        {
            broadcastMuted = !broadcastMuted;
            player.Volume(broadcastMuted ? 0f : volume);
        }
    }

    private void DrawSeek(DirectoryPlayer player)
    {
        var pos = player.Position;
        if (pos is null || pos.Total <= TimeSpan.Zero)
        {
            seeking = false; pendingSeek = null; seekValue = 0f;
            return;
        }

        var total = (float)pos.Total.TotalSeconds;
        var live = (float)pos.Current.TotalSeconds;
        if (pendingSeek is { } target && Math.Abs(live - target) < 0.5)
            pendingSeek = null;
        if (!seeking && pendingSeek is null)
            seekValue = live;

        var shown = TimeSpan.FromSeconds(seeking ? seekValue : live);
        var trailing = $"{UiUtil.FormatTime(shown)} / {UiUtil.FormatTime(pos.Total)}";
        var r = UiUtil.Scrubber(theme, "##seek", ref seekValue, total,
            player.State == PlaybackState.Playing, trailing);

        if (r.Activated) seeking = true;
        if (r.Deactivated)
        {
            player.Seek(TimeSpan.FromSeconds(seekValue));
            pendingSeek = seekValue;
            seeking = false;
        }
    }

    private void DrawLibrary(DirectoryPlayer player)
    {
        var tracks = player.Tracks;
        var hasCurrent = player.State is PlaybackState.Playing or PlaybackState.Paused;
        var currentIndex = hasCurrent ? player.Index : -1;

        if (!ReferenceEquals(tracks, lastTracks) || libFilter != lastLibFilter)
        {
            lastTracks = tracks;
            lastLibFilter = libFilter;
            filteredTracks.Clear();
            var root = player.Directory;
            for (var i = 0; i < tracks.Count; i++)
            {
                var rel = Path.GetRelativePath(root, tracks[i]).Replace('\\', '/');
                if (libFilter.Length != 0 && !rel.Contains(libFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var slash = rel.LastIndexOf('/');
                var name = slash < 0 ? rel : rel[(slash + 1)..];
                var dir = slash < 0 ? "" : rel[..slash];
                filteredTracks.Add((i, name, dir, rel));
            }
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##libfilter", "Search...", ref libFilter, 256);

        var scrollToCurrent = currentIndex != lastScrolledIndex;
        var currentDisplay = -1;
        if (scrollToCurrent && currentIndex >= 0)
            for (var d = 0; d < filteredTracks.Count; d++)
                if (filteredTracks[d].Index == currentIndex) { currentDisplay = d; break; }

        using var hover = ImRaii.PushColor(ImGuiCol.HeaderHovered, theme.Accent with { W = 0.15f })
                                .Push(ImGuiCol.HeaderActive, theme.Accent with { W = 0.25f });

        using var lib = ImRaii.Child("library", Vector2.Zero, true);
        if (!lib.Success || filteredTracks.Count == 0)
            return;

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(filteredTracks.Count);
        if (currentDisplay >= 0)
            clipper.ForceDisplayRangeByIndices(currentDisplay, currentDisplay + 1);
        while (clipper.Step())
            for (var d = clipper.DisplayStart; d < clipper.DisplayEnd; d++)
            {
                var (index, name, dir, rel) = filteredTracks[d];
                var isCurrent = index == currentIndex;

                if (isCurrent)
                {
                    ImGui.PushStyleColor(ImGuiCol.Header, theme.Accent with { W = 0.25f });
                    ImGui.PushStyleColor(ImGuiCol.Text, theme.Accent);
                }
                if (ImGui.Selectable($"{name}##t{index}", isCurrent))
                    player.PlayIndex(index);
                if (isCurrent)
                    ImGui.PopStyleColor(2);

                if (dir.Length > 0)
                {
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(rel);
                    ImGui.SameLine();
                    ImGui.TextDisabled(dir);
                }
                if (scrollToCurrent && d == currentDisplay)
                    ImGui.SetScrollHereY(0.5f);
            }
        clipper.End();
        clipper.Destroy();
        if (scrollToCurrent) lastScrolledIndex = currentIndex;
    }
}
