using System;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Pulsar.Playback;

namespace Pulsar.Windows;

internal sealed class ConfigTab(Plugin plugin, FileDialogManager fileDialogManager, UiTheme theme) : IDisposable
{
    private string inputFolder = "";
    private string outputFolder = "";
    private Task? conversion;
    private CancellationTokenSource? cancellation;
    private ScdConversionProgress progress = new();

    public void Dispose()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Draw()
    {
        using var child = ImRaii.Child("##config-scroll");
        if (!child) return;

        UiUtil.SectionHeader(theme, "VOLUME");
        ImGui.TextDisabled("Mute in-game BGM:");
        DrawCheckbox("While listening to someone else", plugin.Configuration.MuteGameBgmWhileListening, value =>
        {
            plugin.Configuration.MuteGameBgmWhileListening = value;
            plugin.RefreshBgmMute();
        });
        DrawCheckbox("While broadcasting or monitoring locally", plugin.Configuration.MuteGameBgmWhileBroadcasting,
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

        DrawCheckbox("Playback starts while listening volume is muted or zero", plugin.Configuration.NotifyMutedPlayback,
                     value => plugin.Configuration.NotifyMutedPlayback = value);
        DrawCheckbox("When the song changes", plugin.Configuration.NotifyListeningTrackChanged,
                     value => plugin.Configuration.NotifyListeningTrackChanged = value);

        ImGuiHelpers.ScaledDummy(5f);

        DrawCheckbox("A Beefweb source cannot be synchronized", plugin.Configuration.NotifyUnsyncableBroadcast,
                     value => plugin.Configuration.NotifyUnsyncableBroadcast = value);

        UiUtil.SectionHeader(theme, "DEBUG");

        var debug = plugin.Configuration.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debug))
        {
            plugin.Configuration.DebugMode = debug;
            plugin.Configuration.Save();
        }

        DrawScdConverter();
    }

    private void DrawScdConverter()
    {
        UiUtil.SectionHeader(theme, "SCD TO OGG");
        ImGui.TextWrapped("Extract Ogg audio from all SCD files, including subfolders. " +
                          "The output keeps the same folder structure. Existing files are skipped.");
        ImGui.TextWrapped("Only Vorbis SCDs are supported. (This is what most plugins use, except for sound effects.)");

        var running = conversion is { IsCompleted: false };
        using (ImRaii.Disabled(running))
        {
            DrawFolderInput("Input folder", ref inputFolder, path => inputFolder = path);
            DrawFolderInput("Output folder", ref outputFolder, path => outputFolder = path);

            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(inputFolder) || string.IsNullOrWhiteSpace(outputFolder)))
            {
                if (ImGui.Button("Convert SCD files"))
                {
                    cancellation?.Dispose();
                    cancellation = new CancellationTokenSource();
                    var ct = cancellation.Token;
                    var input = inputFolder;
                    var output = outputFolder;
                    var gameData = Plugin.DataManager.GameData;
                    Volatile.Write(ref progress, new ScdConversionProgress());
                    conversion = Task.Run(() => ScdFolderConverter.Convert(input, output,
                        path => ScdReader.ExtractAudio(path, gameData),
                        value => Volatile.Write(ref progress, value), ct), ct);
                }
            }
        }

        if (conversion is null) return;
        var completed = conversion.IsCompleted;
        var snapshot = Volatile.Read(ref progress);
        var status = !completed ? "Converting..." : conversion.IsCanceled ? "Cancelled." :
            conversion.IsFaulted ? "Stopped." : "Finished.";
        ImGui.TextWrapped($"{status} {snapshot.Converted} converted, {snapshot.Skipped} skipped, {snapshot.Failed} failed.");
        if (!completed)
        {
            using var disabled = ImRaii.Disabled(cancellation!.IsCancellationRequested);
            if (ImGui.Button("Cancel conversion")) cancellation.Cancel();
        }
        if (snapshot.LastError is { } error) ImGui.TextWrapped($"Last file error: {error}");
        if (conversion.Exception is { } exception)
            ImGui.TextWrapped($"Conversion error: {exception.GetBaseException().Message}");
    }

    private void DrawFolderInput(string label, ref string folder, Action<string> selected)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(Math.Max(100f, ImGui.GetContentRegionAvail().X - 85f * ImGuiHelpers.GlobalScale));
        ImGui.InputText($"##scd-{label}", ref folder, 4096);
        ImGui.SameLine();
        if (ImGui.Button($"Browse##scd-{label}"))
            fileDialogManager.OpenFolderDialog($"Select SCD {label.ToLowerInvariant()}", (success, path) =>
            {
                if (success) selected(path);
            });
    }

    private void DrawCheckbox(string label, bool value, System.Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return;
        set(value);
        plugin.Configuration.Save();
    }
}
