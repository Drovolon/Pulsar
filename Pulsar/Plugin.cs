using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Pulsar.Windows;
using Pulsar.Listening;
using Pulsar.Ipc;
using Pulsar.Broadcast;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Broadcast.Prepare;
using Pulsar.Common.Api;
using Pulsar.Rpc;

namespace Pulsar;

public sealed class Plugin : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    // These three have internal setters (not private): Pulsar.Tests installs fakes for them.
    [PluginService] internal static IPluginLog Log { get; set; } = null!;
    [PluginService] internal static IFramework Framework { get; set; } = null!;
    [PluginService] internal static IChatGui Chat { get; set; } = null!;

    private const string CommandName = "/pulsar";

    public Configuration Configuration { get; init; }

    public ListeningManager? Listening { get; private set; }
    public BroadcastManager? Broadcast { get; private set; }
    public PenumbraIntegration? Penumbra { get; private set; }
    private SyncPrep? SyncPrep { get; set; }

    // Lightless drives this: SetPlayerData/ClearPlayerData feed the ListeningManager.
    private IpcProvider? Ipc { get; set; }

    public readonly WindowSystem WindowSystem = new("Pulsar");
    private MainWindow MainWindow { get; init; }
    
    private ReconnectingEngine? ListenEngine { get; set; }
    private ReconnectingEngine? BroadcastEngine { get; set; }
    private ReconnectingPrepareService? Prepare { get; set; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        
        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Pulsar UI"
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    private void OnBroadcastingChanged(bool broadcasting)
        => Listening?.SetBroadcasting(broadcasting);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var hostDir = PluginInterface.AssemblyLocation.DirectoryName!;
        var logDir = Path.Combine(PluginInterface.ConfigDirectory.FullName, "logs");
        var audioExe = Path.Combine(hostDir, "Pulsar.AudioHost.exe");

        // Facades are inert until Start(); everything below can safely subscribe and hold
        // references first, then the supervisors bring the hosts online.
        ListenEngine = new ReconnectingEngine(new HostSpec(
            audioExe, PipeNames.AudioListening, logDir, "audio-listening",
            MixerName: "Pulsar (Listening)"));
        BroadcastEngine = new ReconnectingEngine(new HostSpec(
            audioExe, PipeNames.AudioBroadcast, logDir, "audio-broadcast",
            MixerName: "Pulsar (Broadcast)"));
        Prepare = new ReconnectingPrepareService(new HostSpec(
            Path.Combine(hostDir, "Pulsar.TranscodeHost.exe"), PipeNames.TranscodeHost, logDir, "transcode"));

        Listening = new ListeningManager(
            ListenEngine,
            Configuration.ListeningMasterVolume,
            Configuration.ListeningPairVolumes,
            Configuration.ListeningAutoPlay);

        Penumbra = new PenumbraIntegration(PluginInterface);
        SyncPrep = new SyncPrep(
            new CacheManager(Path.Combine(PluginInterface.ConfigDirectory.FullName, "synccache")),
            Prepare);
        Broadcast = new BroadcastManager(BroadcastEngine, Penumbra, SyncPrep, () => Configuration);

        Broadcast.OnBroadcastingChanged += OnBroadcastingChanged;

        ListenEngine.OnReconnected += Listening.OnEngineReconnected;
        BroadcastEngine.OnReconnected += Broadcast.OnEngineReconnected;

        Ipc = new IpcProvider(PluginInterface, Listening, Broadcast);
        Ipc.Prepare();

        ListenEngine.Start();
        BroadcastEngine.Start();
        Prepare.Start();

        try
        {
            if (Configuration.DebugMode)
            {
                // Don't open until fonts are built. I don't want to see the UI without my precious fonts.
                await MainWindow.WaitFontsReadyAsync(cancellationToken);
                ToggleMainUi();
            }

            switch (Configuration.BroadcastMode)
            {
                case BroadcastMode.Folder:
                    // TODO: wire up folder indexes beyond zero (needs UI changes)
                    if (Configuration.BroadcastFolders.Count > 0)
                        await Broadcast.LoadFolder(Configuration.BroadcastFolders[0]);
                    break;
                case BroadcastMode.Mod:
                    if (Configuration.BroadcastMod is { } mod)
                        await Broadcast.LoadMod(mod);
                    break;
                case BroadcastMode.Beefweb:
                    await Broadcast.LoadBeefweb(Configuration.BeefwebPort, Configuration.BeefwebUsername,
                        Configuration.BeefwebPassword, Configuration.BeefwebTransport == BeefwebTransport.Sse);
                    break;
            }
        }
        finally
        {
            // TODO: investigate a better solution for NotifyReady.
            // I noticed Lightless checks whether Dalamud reports the plugin is loaded, in addition to
            // checking whether it's marked as ready. So delay this by a tick to let the plugin be "loaded"
            // before we fire ready. ...There has to be a better way to do this. Hence, the TODO.
            _ = Framework.RunOnTick(Ipc.NotifyReady, delayTicks: 1, cancellationToken: cancellationToken);
        }
    }

    // Things that need to dispose on the framework thread.
    // (Hypothetically - these are copied from SamplePlugin. I'm not *actually* sure they *need* the framework thread.
    // But whatever.)
    public void FrameworkDispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();

        MainWindow.Dispose();
        Penumbra?.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            Ipc?.Dispose();
            Broadcast?.OnBroadcastingChanged -= OnBroadcastingChanged;
            await Framework.RunOnFrameworkThread(FrameworkDispose);
            if (Broadcast is not null) await Broadcast.DisposeAsync();
            if (SyncPrep is not null) await SyncPrep.DisposeAsync();
            if (Listening is not null) await Listening.DisposeAsync();
        }
        finally
        {
            // Always, always, always tear down the subprocesses if we spawned them,
            // even if something else in the dispose path throws some exception.
            if (ListenEngine is not null) await ListenEngine.DisposeAsync();
            if (BroadcastEngine is not null) await BroadcastEngine.DisposeAsync();
            if (Prepare is not null) await Prepare.DisposeAsync();
        }
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    public void ToggleMainUi() => MainWindow.Toggle();
}
