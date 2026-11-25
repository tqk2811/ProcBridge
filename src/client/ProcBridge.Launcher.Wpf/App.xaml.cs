using System.Windows;
using ProcBridge.Crypto.Identity;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Services;
using ProcBridge.Launcher.Wpf.ViewModels;
using ProcBridge.Launcher.Wpf.Views;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Launcher.Wpf;

/// <summary>
/// Composition root. Arguments: <c>--data &lt;dir&gt;</c> (same data folder as the console Launcher: identity key,
/// paired machines, plus <c>profiles.json</c>). Owns the identity key for the whole run.
/// </summary>
public partial class App : Application
{
    private IdentityKey? _identity;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        HorizontalWheelScroll.Register();

        string? dataDirectory = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data" && i + 1 < e.Args.Length)
                dataDirectory = e.Args[++i];
        }
        var data = new LauncherDataStore(dataDirectory);
        _identity = data.OpenIdentity();
        PairedMachineStore pairedMachines = data.OpenPairedMachines();

        var window = new MainWindow();
        var explorerFactory = new SecureRemoteExplorerFactory(_identity, pairedMachines);
        var dialogs = new WpfUserDialogs(window, explorerFactory);
        _main = new MainViewModel(
            new PairedMachineRegistry(_identity, pairedMachines),
            new JsonProfileStore(Path.Combine(data.Directory, "profiles.json")),
            new LaunchSessionRunner(_identity, pairedMachines),
            new LaunchOptionsFactory(AppContext.BaseDirectory),
            explorerFactory,
            dialogs);
        window.DataContext = _main;
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // OnExit is synchronous and the last place to close the explorer connection (quick: closes sockets). Run it off
        // the UI thread so its continuations do not wait for the dispatcher this thread is blocking.
        if (_main is { } main)
            Task.Run(() => main.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));
        _identity?.Dispose();
        base.OnExit(e);
    }
}
