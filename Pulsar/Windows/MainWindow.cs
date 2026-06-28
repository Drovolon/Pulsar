using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Pulsar.Playback;

namespace Pulsar.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialogManager;

    public MainWindow(Plugin plugin)
        : base("Pulsar##pulsar", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;

        this.fileDialogManager = new FileDialogManager();

        this.volume = plugin.Configuration.Volume;
    }

    public void Dispose() { }

    private float volume = 1.0f;
    private bool seeking = false;
    private float? pendingSeek = null;
    private float seekValue = 0f;
    private Jukebox? lastJukebox;
    private int lastScrolledIndex = -1;

    public override void Draw()
    {
        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.Spacing();

        using var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true);

        if (child.Success)
        {
            if (ImGui.Button("Select Folder"))
            {
                fileDialogManager.OpenFolderDialog("Select a music folder", (success, dir) =>
                {
                    if (success) _ = plugin.LoadFolder(dir);
                });
            }

            var jukebox = plugin.CurrentJukebox;

            if (jukebox is not null && !ReferenceEquals(jukebox, lastJukebox))
            {
                jukebox.Volume(volume);
                lastJukebox = jukebox;
            }

            if (jukebox is null)
            {
                ImGui.Text("No folder loaded.");
            }
            else
            {
                if (ImGui.Button("Play")) jukebox.Play();
                ImGui.SameLine();
                if (ImGui.Button("Stop")) jukebox.Stop();
                ImGui.SameLine();
                if (ImGui.Button("Prev")) jukebox.Prev();
                ImGui.SameLine();
                if (ImGui.Button("Next")) jukebox.Next();
                ImGui.SameLine();
                if (ImGui.Button(jukebox.Shuffle ? "Shuffle: On" : "Shuffle: Off"))
                    jukebox.ToggleShuffle();

                ImGui.Spacing();
                if (ImGui.SliderFloat("Volume", ref volume, 0f, 1f, "%.2f"))
                    jukebox.Volume(volume);
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    plugin.Configuration.Volume = volume;
                    plugin.Configuration.Save();
                }

                var pos = jukebox.Position;
                if (pos is { } p && p.Total > TimeSpan.Zero)
                {
                    var total = (float)p.Total.TotalSeconds;
                    var live = (float)p.Current.TotalSeconds;

                    if (pendingSeek is { } target && Math.Abs(live - target) < 0.5)
                        pendingSeek = null;
                    if (!seeking && pendingSeek is null)
                        seekValue = live;

                    ImGui.Text($"{FormatTime(p.Current)} / {FormatTime(p.Total)}");
                    ImGui.SliderFloat("##seek", ref seekValue, 0f, total, "%.0fs");

                    if (ImGui.IsItemActivated()) seeking = true;
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        jukebox.Seek(TimeSpan.FromSeconds(seekValue));
                        pendingSeek = seekValue;
                        seeking = false;
                    }
                    else if (ImGui.IsItemDeactivated()) seeking = false;
                }
                else
                {
                    seeking = false; pendingSeek = null; seekValue = 0f;
                }

                var current = jukebox.CurrentTrack;
                ImGui.Text(jukebox.NowPlaying && current is not null
                    ? $"Now playing: {Path.GetFileName(current)}"
                    : "Not playing");

                ImGui.Separator();

                var tracks = jukebox.Tracks;
                var currentIndex = jukebox.Index;
                var scrollToCurrent = currentIndex != lastScrolledIndex;
                using var lib = ImRaii.Child("library", Vector2.Zero, true);
                if (lib.Success && tracks.Count > 0)
                {
                    var clipper = ImGui.ImGuiListClipper();
                    clipper.Begin(tracks.Count);
                    if (scrollToCurrent && currentIndex >= 0 && currentIndex < tracks.Count)
                        clipper.ForceDisplayRangeByIndices(currentIndex, currentIndex + 1);
                    while (clipper.Step())
                        for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                        {
                            var name = Path.GetFileName(tracks[i]);
                            if (ImGui.Selectable($"{name}##t{i}", i == currentIndex))
                                jukebox.PlayIndex(i);
                            if (scrollToCurrent && i == currentIndex)
                                ImGui.SetScrollHereY(0.5f);
                        }
                    clipper.End();
                    clipper.Destroy();
                    if (scrollToCurrent) lastScrolledIndex = currentIndex;
                }
            }
        }

        fileDialogManager.Draw();
    }

    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
}
