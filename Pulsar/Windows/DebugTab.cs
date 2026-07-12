using System;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Pulsar.Ipc;

namespace Pulsar.Windows;

internal sealed class DebugTab(Plugin plugin, FileDialogManager fileDialogManager)
{
    private bool monitoring;

    private int dbgIdent = 1;
    private string dbgPath = "";
    private bool dbgIsPlaying = true;
    private float dbgPositionSec;
    private int dbgEpoch = 1;

    private string lastQueryResult = "";

    public void Draw()
    {
        DrawMonitor();
        ImGui.Separator();
        ImGui.Spacing();
        DrawIpcTester();
    }

    /// <summary>
    /// Loops the local broadcast back through the inbound IPC, as if we were paired with ourselves.
    /// This lets us test the whole chain locally, with no sync plugin required.
    /// </summary>
    private void DrawMonitor()
    {
        if (ImGui.Checkbox("Loopback broadcast locally", ref monitoring))
            plugin.SetDebugLoopback(monitoring);
    }

    private void DrawIpcTester()
    {
        ImGui.TextDisabled("IPC tester");
        ImGui.Separator();

        ImGui.InputInt("Pair id", ref dbgIdent);

        ImGui.SetNextItemWidth(300);
        ImGui.InputText("##dbgpath", ref dbgPath, 1024);
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.FolderOpen, "Browse"))
        {
            fileDialogManager.OpenFileDialog("Select an audio file",
                "Audio{.aac,.aiff,.flac,.m4a,.mp3,.ogg,.opus,.wav,.wma,.wv}",
                (ok, path) => { if (ok) dbgPath = path; });
        }
        ImGui.SameLine();
        ImGui.Text("File");

        ImGui.Checkbox("Is playing", ref dbgIsPlaying);
        ImGui.InputFloat("Position (s)", ref dbgPositionSec);
        ImGui.InputInt("Cursor epoch", ref dbgEpoch);

        ImGui.Spacing();

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PaperPlane, "Send (new event)"))
        {
            dbgEpoch++;
            SendDebugSet();
        }
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, "Resend (same epoch)")) SendDebugSet();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Ban, "Clear")) SendDebugClear();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Getters");

        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.PowerOff, "IsEnabled")) QueryIsEnabled();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.CodeBranch, "ApiVersion")) QueryApiVersion();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "GetPlayerData")) QueryGetPlayerData();

        if (!string.IsNullOrEmpty(lastQueryResult))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(lastQueryResult);
        }
    }

    private void QueryIsEnabled()
    {
        try
        {
            var enabled = Plugin.PluginInterface
                .GetIpcSubscriber<bool>("Pulsar.IsEnabled")
                .InvokeFunc();
            lastQueryResult = $"IsEnabled: {enabled}";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "IPC tester: IsEnabled failed");
            lastQueryResult = $"IsEnabled: error: {e.Message}";
        }
    }

    private void QueryApiVersion()
    {
        try
        {
            var (major, minor) = Plugin.PluginInterface
                .GetIpcSubscriber<(int, int)>("Pulsar.ApiVersion")
                .InvokeFunc();
            lastQueryResult = $"ApiVersion: {major}.{minor}";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "IPC tester: ApiVersion failed");
            lastQueryResult = $"ApiVersion: error: {e.Message}";
        }
    }

    private void QueryGetPlayerData()
    {
        try
        {
            var data = Plugin.PluginInterface
                .GetIpcSubscriber<(string, string[], string)?>("Pulsar.GetPlayerData")
                .InvokeFunc();
            if (data is null)
            {
                lastQueryResult = "GetPlayerData: null";
                return;
            }

            var (currentFile, prefetch, cursorJson) = data.Value;
            lastQueryResult =
                $"GetPlayerData: \n  file: {currentFile}\n  prefetch: [{string.Join(", ", prefetch)}]\n  cursor: {cursorJson}";
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "IPC tester: GetPlayerData failed");
            lastQueryResult = $"GetPlayerData: error: {e.Message}";
        }
    }

    private void SendDebugSet()
    {
        if (string.IsNullOrWhiteSpace(dbgPath)) return;
        try
        {
            var cursor = new PulsarCursor
            {
                PositionMs = (long)(dbgPositionSec * 1000f),
                IsPlaying = dbgIsPlaying,
                AsOfUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CursorEpoch = dbgEpoch,
            };
            var cursorJson = JsonSerializer.Serialize(cursor);
            Plugin.PluginInterface
                .GetIpcSubscriber<ulong, string, string[], string, object?>("Pulsar.SetPlayerData")
                .InvokeAction((ulong)dbgIdent, dbgPath, [], cursorJson);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "IPC tester: SetPlayerData failed");
        }
    }

    private void SendDebugClear()
    {
        try
        {
            Plugin.PluginInterface
                .GetIpcSubscriber<ulong, object?>("Pulsar.ClearPlayerData")
                .InvokeAction((ulong)dbgIdent);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "IPC tester: ClearPlayerData failed");
        }
    }
}
