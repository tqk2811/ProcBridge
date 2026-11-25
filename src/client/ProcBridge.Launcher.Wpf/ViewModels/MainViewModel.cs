using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Services;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Root of the main window: Machines B, Profiles (with Run) and the Remote Explorer tabs.</summary>
public sealed class MainViewModel : IAsyncDisposable
{
    public MainViewModel(IPairedMachineRegistry registry, IProfileStore profiles, ILaunchRunner runner, LaunchOptionsFactory optionsFactory,
        IRemoteExplorerFactory explorerFactory, IUserDialogs dialogs)
    {
        Machines = new MachinesViewModel(registry, dialogs);
        Run = new RunViewModel(runner, optionsFactory);
        Profiles = new ProfilesViewModel(profiles, dialogs, Machines, Run, explorerFactory);
        Explorer = new RemoteExplorerViewModel(Machines, explorerFactory, dialogs);
    }

    public MachinesViewModel Machines { get; }

    public ProfilesViewModel Profiles { get; }

    public RunViewModel Run { get; }

    public RemoteExplorerViewModel Explorer { get; }

    public ValueTask DisposeAsync() => Explorer.DisposeAsync();
}
