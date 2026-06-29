using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Pulsar.Windows;
using Pulsar.Playback;
using System;
using System.Threading.Tasks;
using System.Threading;

namespace Pulsar;

public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string CommandName = "/pulsar";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("Pulsar");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    private volatile DirectoryPlayer? player;
    public DirectoryPlayer? CurrentPlayer => player;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        if (Configuration.DebugMode)
            ToggleMainUi();

        return Task.CompletedTask;
    }

    // Builds a fresh player for the chosen folder, replacing any current one.
    public async Task LoadFolder(string directory)
    {
        try
        {
            var old = player;
            player = null;
            if (old is not null) await old.DisposeAsync();
            var p = new DirectoryPlayer(directory);
            await p.Initialize();
            player = p;
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to load folder");
        }
    }

    // Things that need to dispose on the framework thread.
    // (Hypothetically - these are from SamplePlugin. I'm not
    // *actually* sure they *need* the framework thread.
    // But whatever.)
    public void FrameworkDispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    public async ValueTask DisposeAsync()
    {
        await Framework.RunOnFrameworkThread(FrameworkDispose);
        if (player is not null) await player.DisposeAsync();
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
