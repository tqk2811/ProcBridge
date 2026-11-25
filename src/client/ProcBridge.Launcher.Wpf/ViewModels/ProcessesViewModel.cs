using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Process tab: processes of machine B; Kill (whole tree) only with <see cref="ExplorerPermissions.ProcessKill"/>.</summary>
public sealed class ProcessesViewModel : ExplorerPageViewModel
{
    private ProcessEntry? _selectedProcess;

    public ProcessesViewModel(IRemoteExplorer explorer, ExplorerPermissions granted, IUserDialogs dialogs)
        : base(explorer, granted, dialogs, ExplorerPermissions.ProcessRead)
    {
        KillCommand = new AsyncRelayCommand(KillAsync, () => CanKill && SelectedProcess is not null);
    }

    public ObservableCollection<ProcessEntry> Processes { get; } = [];

    public bool CanKill => Granted.HasFlag(ExplorerPermissions.ProcessKill);

    public AsyncRelayCommand KillCommand { get; }

    public ProcessEntry? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (SetProperty(ref _selectedProcess, value))
                KillCommand.NotifyCanExecuteChanged();
        }
    }

    protected override async Task<ExplorerResponse> LoadCoreAsync()
    {
        ProcessListResponse response = await Explorer.ListProcessesAsync(CancellationToken.None);
        Processes.Clear();
        foreach (ProcessEntry process in response.Processes.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Pid))
            Processes.Add(process);
        return response;
    }

    private Task KillAsync()
    {
        if (!CanKill || SelectedProcess is not { } process)
            return Task.CompletedTask;
        return ConfirmAndRunAsync("Kill process", $"Kill {process.Name} (PID {process.Pid}) và cả cây con của nó trên máy B?",
            () => Explorer.KillProcessAsync(process.Pid, entireTree: true, CancellationToken.None));
    }
}
