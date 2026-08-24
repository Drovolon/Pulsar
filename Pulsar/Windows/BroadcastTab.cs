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
using Pulsar.Broadcast.Local;
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
    private LocalSource? lastPlayer;
    private int lastScrolledIndex = -1;
    private int selectedLibraryIndex = -1;
    private QueueEntryId? selectedQueueId;

    // search filter. note: doesn't affect playlist, just the results table in UI
    private string libFilter = "";
    private IReadOnlyList<LocalTrack>? lastTracks;
    private string lastLibFilter = "";
    // Name = catalog display name; Dir = folder relative to the catalog root ("" for root-level
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

        DrawOnAir();

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
                plugin.Broadcast?.SetProvider(BroadcastProvider.Local);
                var folders = plugin.Configuration.BroadcastFolders;
                if (folders.Count > 0) _ = plugin.Broadcast?.LoadFolder(folders[0]);
                break;
            case BroadcastMode.Mod:
                plugin.Broadcast?.SetProvider(BroadcastProvider.Local);
                if (plugin.Configuration.BroadcastMod is { } mod)
                    _ = plugin.Broadcast?.LoadMod(mod, plugin.Configuration.BroadcastModGroup);
                break;
            case BroadcastMode.Beefweb:
                ReloadBeefweb();
                break;
        }
    }

    private void DrawOnAir()
    {
        UiUtil.SectionHeader(theme, "BROADCAST");
        var broadcast = plugin.Broadcast;
        var onAir = broadcast?.OnAir ?? false;
        if (ImGui.Checkbox("On Air", ref onAir)) broadcast?.SetOnAir(onAir);

        ImGui.SameLine();
        var status = broadcast?.BroadcastStatus;
        var text = status switch
        {
            null => "Not ready.",
            { Phase: BroadcastPhase.OffAir } => "Not currently broadcasting",
            { Phase: BroadcastPhase.Failed, LiveProvider: { } live } =>
                $"Couldn't prepare {ProviderLabel(status.DesiredProvider)}; still broadcasting from {ProviderLabel(live)}.",
            { Phase: BroadcastPhase.Failed } =>
                $"Couldn't prepare {ProviderLabel(status.DesiredProvider)}.",
            { Phase: BroadcastPhase.Retrying, LiveProvider: { } live } =>
                $"Retrying {ProviderLabel(status.DesiredProvider)}; still broadcasting from {ProviderLabel(live)}.",
            { Phase: BroadcastPhase.Retrying } =>
                $"Retrying {ProviderLabel(status.DesiredProvider)}...",
            { Phase: BroadcastPhase.Switching, LiveProvider: { } live } =>
                $"Broadcasting from {ProviderLabel(live)}; switching to {ProviderLabel(status.DesiredProvider)}...",
            { Phase: BroadcastPhase.Live, LiveProvider: { } live } =>
                $"Broadcasting from {ProviderLabel(live)}.",
            _ => $"Enabling {ProviderLabel(status.DesiredProvider)} playback...",
        };
        ImGui.TextDisabled(text);
        if (status is { Phase: BroadcastPhase.Failed })
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Retry##broadcast-handoff"))
                broadcast?.RetryHandoff();
        }
    }

    private static string ProviderLabel(BroadcastProvider provider)
        => provider == BroadcastProvider.Local ? "Pulsar queue" : "Beefweb";

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
            if (plugin.Broadcast?.ActiveLocalSource is { } source) _ = source.Rescan();
        }

        var savedFolder = plugin.Configuration.BroadcastFolders.Count > 0
            ? plugin.Configuration.BroadcastFolders[0]
            : null;
        ImGui.SameLine();
        ImGui.TextDisabled(savedFolder ?? "(no folder)");

        DrawBrowserStatus();
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
            if (plugin.Broadcast?.ActiveLocalSource is { ModDirectoryName: not null } source)
                _ = source.Rescan();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1f);
        if (modCombo.Draw("##mod", preview, mods, current, out var picked))
        {
            selectedDir = picked!.DirectoryName;
            plugin.Configuration.BroadcastMod = selectedDir;
            plugin.Configuration.BroadcastModGroup = null;
            plugin.Configuration.Save();
            _ = plugin.Broadcast?.LoadMod(selectedDir, null);
        }

        DrawModGroup(selectedDir);

        DrawBrowserStatus();
        DrawPlayer();
    }

    private void DrawBrowserStatus()
    {
        var status = plugin.Broadcast?.BrowserLoad;
        if (status is { Loading: true }) ImGui.TextDisabled("Loading library...");
        else if (status?.Error is { } error)
            ImGui.TextColored(new Vector4(0.85f, 0.25f, 0.25f, 1f), $"Library load failed: {error}");
    }

    private void DrawModGroup(string? selectedMod)
    {
        var source = plugin.Broadcast?.ActiveLocalSource;
        if (source?.ModDirectoryName != selectedMod) source = null;

        var sourceView = source?.View;
        var selected = sourceView?.Browser.SelectedGroup;
        var preview = selected?.Name ?? TrackCatalog.AllFilesName;
        using (ImRaii.Disabled(sourceView is null))
        {
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.BeginCombo("##modgroup", preview))
            {
                if (sourceView is not null)
                {
                    foreach (var group in sourceView.Browser.Catalog.Groups)
                    {
                        var isSelected = group.Id == selected!.Id;
                        if (ImGui.Selectable($"{group.Name} ({group.Tracks.Count})", isSelected) && !isSelected)
                        {
                            _ = source!.SelectGroup(group.Id);
                            selected = group;
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                }
                ImGui.EndCombo();
            }
        }

        if (sourceView is not null)
        {
            var selectedId = sourceView.Browser.SelectedGroup.Id;
            var actual = selectedId == TrackCatalog.AllFilesId ? null : selectedId;
            if (plugin.Configuration.BroadcastModGroup != actual)
            {
                plugin.Configuration.BroadcastModGroup = actual;
                plugin.Configuration.Save();
            }
        }
    }

    private void ReloadBeefweb()
    {
        var c = plugin.Configuration;
        _ = plugin.Broadcast?.LoadBeefweb(c.BeefwebPort, c.BeefwebUsername, c.BeefwebPassword,
            c.BeefwebTransport);
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
        if (plugin.Broadcast?.ActiveBeefweb?.Status.Connected != true) return;

        UiUtil.SectionHeader(theme, "CONTROLS");

        var snap = plugin.Broadcast?.ActiveBeefweb?.Current;
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

        var label = !string.IsNullOrEmpty(snap.Meta.DisplayName) ? snap.Meta.DisplayName
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

    // Shared transport + library controls for whatever local source is active (folder or mod).
    private void DrawPlayer()
    {
        var player = plugin.Broadcast?.ActiveLocalSource;

        if (player is not null && !ReferenceEquals(player, lastPlayer))
        {
            player.Volume(broadcastMuted ? 0f : volume);
            lastPlayer = player;
        }

        if (player is null)
        {
            ImGui.TextDisabled("Nothing loaded.");
            return;
        }

        UiUtil.SectionHeader(theme, "PLAYER");

        DrawControls(player);
        DrawSeek(player);
        DrawUpNext(player);

        UiUtil.SectionHeader(theme, "LIBRARY");
        DrawLibrary(player);
    }

    private void DrawUpNext(LocalSource player)
    {
        var queue = player.Queue;
        var upcoming = player.UpNext;
        var selectedIndex = selectedQueueId is { } selected
            ? Enumerable.Range(0, queue.Count).FirstOrDefault(
                i => queue[i].Id == selected, -1)
            : -1;
        if (selectedIndex < 0) selectedQueueId = null;

        using (ImRaii.Disabled(selectedQueueId is null))
        {
            using (ImRaii.Disabled(selectedIndex <= 0))
                if (UiUtil.IconButton("queueup", FontAwesomeIcon.ArrowUp, "Move up")
                    && selectedQueueId is { } moveUp)
                    player.Move(moveUp, -1);
            ImGui.SameLine();
            using (ImRaii.Disabled(selectedIndex < 0 || selectedIndex + 1 >= queue.Count))
                if (UiUtil.IconButton("queuedown", FontAwesomeIcon.ArrowDown, "Move down")
                    && selectedQueueId is { } moveDown)
                    player.Move(moveDown, 1);
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(selectedQueueId is null))
            if (UiUtil.IconButton("queueremove", FontAwesomeIcon.Trash, "Remove from manual queue")
                && selectedQueueId is { } remove)
            {
                player.Remove(remove);
                selectedQueueId = null;
            }
        ImGui.SameLine();
        using (ImRaii.Disabled(upcoming.Count(item => !item.IsQueued) < 2))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Random, "Shuffle upcoming"))
                player.ShuffleUpcoming();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shuffle source tracks (doesn't affect manual queue)");
        ImGui.SameLine();
        using (ImRaii.Disabled(queue.Count == 0))
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Ban, "Clear"))
            {
                player.ClearQueue();
                selectedQueueId = null;
            }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Clear manual queue (source playback continues)");

        var upNextHeight = (6f * ImGui.GetTextLineHeightWithSpacing())
                           + (2f * ImGui.GetStyle().FramePadding.Y);
        using var queueChild = ImRaii.Child("upnext", new Vector2(0, upNextHeight), true);
        if (!queueChild.Success) return;

        var current = player.State is PlaybackState.Playing or PlaybackState.Paused
            ? player.CurrentTrack
            : null;
        if (current is not null)
        {
            var rowY = ImGui.GetCursorPosY();
            DrawPlaybackIcon(FontAwesomeIcon.Music, theme.Accent, rowY);
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
            ImGui.TextColored(theme.Accent, current.DisplayName);
        }
        else
        {
            var rowY = ImGui.GetCursorPosY();
            DrawPlaybackIcon(FontAwesomeIcon.Stop, theme.Neutral, rowY);
            ImGui.SameLine();
            ImGui.SetCursorPosY(rowY);
            ImGui.TextDisabled("Not playing");
        }

        if (upcoming.Count == 0)
            return;

        for (var i = 0; i < upcoming.Count; i++)
        {
            var item = upcoming[i];
            var track = item.Track;
            var prefix = $"{i + 1}.";
            if (item.QueueEntryId is { } id)
            {
                var label = $"{prefix} {track.DisplayName}";
                var labelPos = ImGui.GetCursorScreenPos();
                if (ImGui.Selectable($"{label}  ##q{id.Value}", selectedQueueId == id))
                    selectedQueueId = id;
                var queuedTagX = labelPos.X + ImGui.CalcTextSize($"{label}  ").X;
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(queuedTagX, labelPos.Y),
                    ImGui.GetColorU32(theme.QueueAccent),
                    "[queued]");
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    player.Play(id);
                    selectedQueueId = null;
                }
                if (ImGui.BeginPopupContextItem($"upnextctx{id.Value}"))
                {
                    if (ImGui.MenuItem("Play now"))
                    {
                        player.Play(id);
                        selectedQueueId = null;
                    }
                    if (ImGui.MenuItem("Remove from queue"))
                    {
                        player.Remove(id);
                        selectedQueueId = null;
                    }
                    ImGui.EndPopup();
                }
            }
            else
            {
                ImGui.TextDisabled($"{prefix} {track.DisplayName}");
                if (ImGui.BeginPopupContextItem($"upnextsource{i}"))
                {
                    if (ImGui.MenuItem("Play now")) player.PlayFromActiveSource(track);
                    if (ImGui.MenuItem("Play next")) player.AddNext(track);
                    ImGui.EndPopup();
                }
            }
        }
    }

    private void DrawControls(LocalSource player)
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

    private void DrawSeek(LocalSource player)
    {
        var pos = player.Position;
        if (pos is null || pos.Total <= TimeSpan.Zero)
        {
            seeking = false; pendingSeek = null; seekValue = 0f;
            var empty = 0f;
            using var disabled = ImRaii.Disabled();
            UiUtil.Scrubber(theme, "##seek", ref empty, 1f, false, "0:00 / 0:00");
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

    private void DrawLibrary(LocalSource player)
    {
        var tracks = player.Library;
        var currentPath = player.State is PlaybackState.Playing or PlaybackState.Paused
            ? player.CurrentTrack?.FilePath
            : null;

        var tracksChanged = !ReferenceEquals(tracks, lastTracks);
        if (tracksChanged || libFilter != lastLibFilter)
        {
            if (tracksChanged)
            {
                lastScrolledIndex = -1;
                selectedLibraryIndex = -1;
            }
            lastTracks = tracks;
            lastLibFilter = libFilter;
            filteredTracks.Clear();
            for (var i = 0; i < tracks.Count; i++)
            {
                var rel = tracks[i].RelativePath;
                if (libFilter.Length != 0
                    && !rel.Contains(libFilter, StringComparison.OrdinalIgnoreCase)
                    && !tracks[i].DisplayName.Contains(libFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                var slash = rel.LastIndexOf('/');
                var name = tracks[i].DisplayName;
                var dir = slash < 0 ? "" : rel[..slash];
                filteredTracks.Add((i, name, dir, rel));
            }
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##libfilter", "Search...", ref libFilter, 256);

        var queueIndexes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < player.Queue.Count; i++)
        {
            var path = player.Queue[i].Track.FilePath;
            if (!queueIndexes.TryGetValue(path, out var indexes))
                queueIndexes[path] = indexes = [];
            indexes.Add(i + 1);
        }

        if (selectedLibraryIndex >= tracks.Count) selectedLibraryIndex = -1;
        var setActiveWidth = ImGuiComponents.GetIconButtonWithTextWidth(
            FontAwesomeIcon.Check, "Set as Active");
        var setActiveX = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - setActiveWidth;
        using (ImRaii.Disabled(selectedLibraryIndex < 0))
        {
            var selected = selectedLibraryIndex >= 0 ? tracks[selectedLibraryIndex] : null;
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Plus, "Add to queue") && selected is not null)
                player.AddToEnd(selected);
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.StepForward, "Play next") && selected is not null)
                player.AddNext(selected);
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Play now") && selected is not null)
                player.PlayNow(selected);
        }
        if (!player.View.Browser.SelectedGroupIsActive)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), setActiveX));
            using (ImRaii.Disabled(tracks.Count == 0))
            {
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Check, "Set as Active"))
                    player.SetActiveSource();
            }
        }

        var currentIndex = currentPath is null
            ? -1
            : Enumerable.Range(0, tracks.Count).FirstOrDefault(
                i => string.Equals(tracks[i].FilePath, currentPath, StringComparison.OrdinalIgnoreCase), -1);
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

        var playingIcon = FontAwesomeIcon.Music.ToIconString();
        ImFontPtr playingIconFont;
        float playingIconFontSize;
        using (theme.IconTextFont.Push())
        {
            playingIconFont = ImGui.GetFont();
            playingIconFontSize = ImGui.GetFontSize();
        }
        float markerWidth;
        float queueAfterIcon;
        using (theme.IconTextFont.Push())
        {
            markerWidth = ImGui.CalcTextSize(playingIcon).X;
            queueAfterIcon = ImGui.CalcTextSize($"{playingIcon} ").X;
            foreach (var positions in queueIndexes.Values)
                markerWidth = MathF.Max(
                    markerWidth,
                    ImGui.CalcTextSize(QueuePositionsLabel(positions)).X);
            if (currentIndex >= 0)
            {
                var currentQueued = QueuePositionsLabel(
                    queueIndexes.GetValueOrDefault(tracks[currentIndex].FilePath));
                if (currentQueued.Length > 0)
                    markerWidth = MathF.Max(
                        markerWidth,
                        ImGui.CalcTextSize($"{playingIcon} {currentQueued}").X);
            }
        }

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
                var rowX = ImGui.GetCursorPosX();
                var markerX = ImGui.GetCursorScreenPos().X;
                var queued = QueuePositionsLabel(
                    queueIndexes.GetValueOrDefault(tracks[index].FilePath));
                ImGui.SetCursorPosX(rowX + markerWidth + ImGui.GetStyle().ItemSpacing.X);
                var textY = ImGui.GetCursorScreenPos().Y;
                if (ImGui.Selectable($"{name}##t{index}", selectedLibraryIndex == index))
                    selectedLibraryIndex = index;
                var drawList = ImGui.GetWindowDrawList();
                if (isCurrent)
                    drawList.AddText(
                        playingIconFont,
                        playingIconFontSize,
                        new Vector2(markerX, textY),
                        ImGui.GetColorU32(theme.Accent),
                        playingIcon);
                if (queued.Length > 0)
                    drawList.AddText(
                        playingIconFont,
                        playingIconFontSize,
                        new Vector2(markerX + (isCurrent ? queueAfterIcon : 0f), textY),
                        ImGui.GetColorU32(theme.QueueAccent),
                        queued);
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    player.PlayNow(tracks[index]);
                if (ImGui.BeginPopupContextItem($"trackctx{index}"))
                {
                    if (ImGui.MenuItem("Add to queue")) player.AddToEnd(tracks[index]);
                    if (ImGui.MenuItem("Play next")) player.AddNext(tracks[index]);
                    if (ImGui.MenuItem("Play now")) player.PlayNow(tracks[index]);
                    ImGui.EndPopup();
                }
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

    internal static string QueuePositionsLabel(IReadOnlyList<int>? queuePositions)
        => queuePositions is { Count: > 0 }
            ? $"[{string.Join(",", queuePositions)}]"
            : "";

    private void DrawPlaybackIcon(FontAwesomeIcon icon, Vector4 color, float rowY)
    {
        var rowHeight = ImGui.GetTextLineHeight();
        using (theme.IconTextFont.Push())
        {
            var iconHeight = ImGui.GetTextLineHeight();
            ImGui.SetCursorPosY(rowY + MathF.Max(0f, (rowHeight - iconHeight) * 0.5f));
            ImGui.TextColored(color, icon.ToIconString());
        }
    }
}
