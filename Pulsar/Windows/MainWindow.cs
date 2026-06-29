using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Pulsar.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    private readonly FileDialogManager fileDialogManager;

    private readonly UiTheme theme;

    private readonly ListeningTab listeningTab;
    private readonly BroadcastTab broadcastTab;
    private readonly ConfigTab configTab;
    private readonly DebugTab debugTab;

    public MainWindow(Plugin plugin)
        : base("Pulsar##pulsar", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
        fileDialogManager = new FileDialogManager();
        theme = new UiTheme(Plugin.PluginInterface);

        listeningTab = new ListeningTab(plugin, theme);
        broadcastTab = new BroadcastTab(plugin, fileDialogManager, theme);
        configTab = new ConfigTab(plugin);
        debugTab = new DebugTab(plugin, fileDialogManager);
    }

    public void Dispose() => theme.Dispose();

    public Task WaitFontsReadyAsync(CancellationToken ct) => theme.WaitFontsReadyAsync(ct);

    public override void Draw()
    {
        using (var tabs = ImRaii.TabBar("##pulsar_tabs"))
        {
            if (tabs)
            {
                DrawTab($"{FontAwesomeIcon.Headphones.ToIconString()}Listening##listening", listeningTab.Draw);
                DrawTab($"{FontAwesomeIcon.BroadcastTower.ToIconString()}Broadcast##broadcast", broadcastTab.Draw);
                DrawTab($"{FontAwesomeIcon.Cog.ToIconString()}Config##config", configTab.Draw);
                if (plugin.Configuration.DebugMode)
                    DrawTab($"{FontAwesomeIcon.Wrench.ToIconString()}Debug##debug", debugTab.Draw);
            }
        }

        fileDialogManager.Draw();
    }

    private void DrawTab(string label, Action draw)
    {
        bool open;
        using (theme.IconTextFont.Push())
            open = ImGui.BeginTabItem(label);
        if (open)
        {
            draw();
            ImGui.EndTabItem();
        }
    }
}
