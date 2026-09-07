using System;
using System.Globalization;
using System.Numerics;
using System.Reflection;
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

    public MainWindow(Plugin plugin) : base(GetWindowName(),
                                            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(450, 400),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        fileDialogManager = new FileDialogManager();
        theme = new UiTheme(Plugin.PluginInterface);

        listeningTab = new ListeningTab(plugin, theme);
        broadcastTab = new BroadcastTab(plugin, fileDialogManager, theme);
        configTab = new ConfigTab(plugin, fileDialogManager, theme);
        debugTab = new DebugTab(plugin, fileDialogManager);
    }

    private static string GetWindowName()
    {
#if DEBUG
        return $"Pulsar Dev Build ({GetTitlebarBuildVersion()})###pulsar";
#else
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null
            ? "Pulsar###pulsar"
            : $"Pulsar {version.Major}.{version.Minor}.{version.Build}.{version.Revision}###pulsar";
#endif
    }

    private static string GetTitlebarBuildVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion)) return FormatTitlebarBuildVersion(informationalVersion);

        var version = assembly.GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    private static string FormatTitlebarBuildVersion(string informationalVersion)
    {
        const string dirtySuffix = "-dirty";

        var isDirty = informationalVersion.EndsWith(dirtySuffix, StringComparison.Ordinal);
        if (isDirty) informationalVersion = informationalVersion[..^dirtySuffix.Length];

        var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (metadataIndex < 0 || metadataIndex >= informationalVersion.Length - 1)
            return isDirty ? informationalVersion + "*" : informationalVersion;

        var version = informationalVersion[..metadataIndex];
        var sourceRevision = informationalVersion[(metadataIndex + 1)..];
        var hashIndex = sourceRevision.LastIndexOf("-g", StringComparison.Ordinal);
        var countIndex = hashIndex > 0 ? sourceRevision.LastIndexOf('-', hashIndex - 1) : -1;
        var displayVersion =
            countIndex >= 0 &&
            int.TryParse(sourceRevision[(countIndex + 1)..hashIndex], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? $"{version}-{sourceRevision[(countIndex + 1)..]}"
                : $"{version}+{sourceRevision}";

        return isDirty ? displayVersion + "*" : displayVersion;
    }

    public void Dispose()
    {
        fileDialogManager.Reset();
        configTab.Dispose();
        theme.Dispose();
    }

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
        {
            open = ImGui.BeginTabItem(label);
        }

        if (!open) return;
        try
        {
            draw();
        } finally
        {
            ImGui.EndTabItem();
        }
    }
}
