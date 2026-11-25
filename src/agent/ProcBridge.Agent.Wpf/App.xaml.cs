using System.IO;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ProcBridge.Agent.Wpf.Core;
using ProcBridge.Agent.Wpf.Core.ViewModels;
using ProcBridge.Agent.Wpf.Services;
using ProcBridge.Agent.Wpf.Views;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Wpf;

/// <summary>
/// Agent app of machine B. Arguments: <c>[--data &lt;dir&gt;]</c> (default <c>%LOCALAPPDATA%\ProcBridge\agent</c>, the same
/// folder as the console Agent, so both share the identity, the paired machines and the Explorer profiles).
/// </summary>
public partial class App : Application
{
    private string _dataDirectory = AgentDataFiles.DefaultDirectory;
    private IdentityKey? _identity;
    private AgentHost? _host;

    private string LogPath => Path.Combine(_dataDirectory, "agent-ui.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data" && i + 1 < e.Args.Length)
                    _dataDirectory = Path.GetFullPath(e.Args[++i]);
            }
            ScrollHelper.RegisterShiftWheelHorizontalScroll();

            var protector = new DpapiKeyProtector();
            _identity = new IdentityKeyStore(Path.Combine(_dataDirectory, AgentDataFiles.IdentityKey), protector).LoadOrCreate();
            var pairedMachines = new PairedMachineStore(Path.Combine(_dataDirectory, AgentDataFiles.PairedMachines), protector);
            var explorerPolicies = new ExplorerPolicyStore(Path.Combine(_dataDirectory, AgentDataFiles.ExplorerPolicies), protector);
            _host = new AgentHost(_identity, pairedMachines, explorerPolicies);
            var window = new MainWindow();
            var dialogs = new WpfDialogService(window);
            var viewModel = new MainViewModel(_host, pairedMachines, explorerPolicies,
                new JsonAgentSettingsStore(Path.Combine(_dataDirectory, JsonAgentSettingsStore.FileName)), dialogs, IsElevated());
            window.Attach(viewModel);
            window.Show();
            if (viewModel.StartServerOnLaunch)
                viewModel.Start();
        }
        catch (Exception ex)
        {
            Log("startup", ex);
            MessageBox.Show($"Không khởi động được ProcBridge Agent:\n{ex.Message}", "ProcBridge Agent", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _identity?.Dispose();
        base.OnExit(e);
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("ui", e.Exception);
        MessageBox.Show(e.Exception.Message, "ProcBridge Agent", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => Log("fatal", e.ExceptionObject as Exception);

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("task", e.Exception);
        e.SetObserved();
    }

    /// <summary>Synchronous append, so a fatal error is on disk before the process dies.</summary>
    private void Log(string context, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{context}] {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing left to report to.
        }
    }
}
