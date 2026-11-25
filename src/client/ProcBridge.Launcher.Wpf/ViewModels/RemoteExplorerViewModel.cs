using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// Remote Explorer tab (plan section 14): connects to one paired machine B, reads the profile machine B granted
/// (<see cref="ExplorerPolicyResponse"/>) and shows File / Process / Registry / Service / System pages. Owns the explorer
/// of the current connection.
/// </summary>
public sealed class RemoteExplorerViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IRemoteExplorerFactory _factory;
    private readonly IUserDialogs _dialogs;
    private IRemoteExplorer? _explorer;
    private PairedMachine? _selectedMachine;
    private ExplorerPermissions _permissions;
    private string? _statusText;
    private bool _isConnected;
    private RemoteFolderTreeViewModel? _files;
    private ProcessesViewModel? _processes;
    private RegistryViewModel? _registry;
    private ServicesViewModel? _services;
    private SystemInfoViewModel? _systemInfo;

    public RemoteExplorerViewModel(MachinesViewModel machines, IRemoteExplorerFactory factory, IUserDialogs dialogs)
    {
        Machines = machines;
        _factory = factory;
        _dialogs = dialogs;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => SelectedMachine is not null);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        DeleteFileCommand = new AsyncRelayCommand(DeleteFileAsync, p => CanWriteFiles && p is FileEntry);
    }

    public MachinesViewModel Machines { get; }

    public AsyncRelayCommand ConnectCommand { get; }

    public AsyncRelayCommand DisconnectCommand { get; }

    /// <summary>Parameter: the <see cref="FileEntry"/> to delete (files of the selected folder).</summary>
    public AsyncRelayCommand DeleteFileCommand { get; }

    public PairedMachine? SelectedMachine
    {
        get => _selectedMachine;
        set
        {
            if (SetProperty(ref _selectedMachine, value))
                ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    public ExplorerPermissions Permissions
    {
        get => _permissions;
        private set
        {
            if (SetProperty(ref _permissions, value))
                OnPropertyChanged(nameof(CanWriteFiles));
        }
    }

    public bool CanWriteFiles => _permissions.HasFlag(ExplorerPermissions.FileWrite);

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public RemoteFolderTreeViewModel? Files
    {
        get => _files;
        private set => SetProperty(ref _files, value);
    }

    public ProcessesViewModel? Processes
    {
        get => _processes;
        private set => SetProperty(ref _processes, value);
    }

    public RegistryViewModel? Registry
    {
        get => _registry;
        private set => SetProperty(ref _registry, value);
    }

    public ServicesViewModel? Services
    {
        get => _services;
        private set => SetProperty(ref _services, value);
    }

    public SystemInfoViewModel? SystemInfo
    {
        get => _systemInfo;
        private set => SetProperty(ref _systemInfo, value);
    }

    /// <summary>Connects to <see cref="SelectedMachine"/> and loads every page; never throws.</summary>
    public async Task ConnectAsync()
    {
        if (SelectedMachine is not { } machine)
            return;
        await DisconnectAsync();
        StatusText = $"Đang kết nối {machine.Name} ({machine.Address})…";
        IRemoteExplorer explorer = _factory.Create(machine);
        ExplorerPolicyResponse policy;
        try
        {
            policy = await explorer.GetPolicyAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            await explorer.DisposeAsync();
            StatusText = $"Không kết nối được tới máy B: {e.Message}";
            return;
        }
        _explorer = explorer;
        Permissions = policy.Permissions;
        Files = new RemoteFolderTreeViewModel(explorer);
        Processes = new ProcessesViewModel(explorer, policy.Permissions, _dialogs);
        Registry = new RegistryViewModel(explorer, policy.Permissions, _dialogs, policy.RegistryRoots);
        Services = new ServicesViewModel(explorer, policy.Permissions, _dialogs);
        SystemInfo = new SystemInfoViewModel(explorer, policy.Permissions, _dialogs);
        IsConnected = true;
        StatusText = policy.Permissions == ExplorerPermissions.None
            ? $"Đã kết nối {machine.Name}, nhưng máy B chưa cấp quyền nào cho máy này."
            : $"Đã kết nối {machine.Name}. Quyền máy B cấp: {policy.Permissions}.";

        if (policy.Permissions.HasFlag(ExplorerPermissions.FileRead))
            await Files.LoadAsync();
        await Task.WhenAll(Processes.LoadAsync(), Registry.LoadAsync(), Services.LoadAsync(), SystemInfo.LoadAsync());
    }

    public async Task DisconnectAsync()
    {
        IRemoteExplorer? explorer = _explorer;
        _explorer = null;
        Files = null;
        Processes = null;
        Registry = null;
        Services = null;
        SystemInfo = null;
        Permissions = ExplorerPermissions.None;
        IsConnected = false;
        if (explorer is not null)
        {
            await explorer.DisposeAsync();
            StatusText = "Đã ngắt kết nối.";
        }
    }

    private async Task DeleteFileAsync(object? parameter)
    {
        if (!CanWriteFiles || parameter is not FileEntry entry || _explorer is not { } explorer || Files?.SelectedNode is not { } folder)
            return;
        if (!_dialogs.Confirm("Xoá file", $"Xoá {entry.FullPath} trên máy B?"))
            return;
        try
        {
            ExplorerAckResponse ack = await explorer.DeleteAsync(entry.FullPath, recursive: false, CancellationToken.None);
            StatusText = ack.Status == ExplorerStatus.Ok ? $"Đã xoá {entry.FullPath}." : $"Không xoá được: {ExplorerStatusText.Describe(ack.Status, ack.Error)}";
            if (ack.Status == ExplorerStatus.Ok)
                await folder.ReloadAsync();
        }
        catch (Exception e)
        {
            StatusText = $"Không xoá được: mất kết nối tới máy B: {e.Message}";
        }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
