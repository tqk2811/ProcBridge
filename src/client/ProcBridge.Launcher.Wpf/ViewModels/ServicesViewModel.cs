using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Service tab: services of machine B; Start/Stop/Restart only with <see cref="ExplorerPermissions.ServiceControl"/>.</summary>
public sealed class ServicesViewModel : ExplorerPageViewModel
{
    private ServiceEntry? _selectedService;

    public ServicesViewModel(IRemoteExplorer explorer, ExplorerPermissions granted, IUserDialogs dialogs)
        : base(explorer, granted, dialogs, ExplorerPermissions.ServiceRead)
    {
        ControlCommand = new AsyncRelayCommand(p => ControlAsync(p), p => CanControl && SelectedService is not null && p is ServiceControlAction);
    }

    public ObservableCollection<ServiceEntry> Services { get; } = [];

    public bool CanControl => Granted.HasFlag(ExplorerPermissions.ServiceControl);

    /// <summary>Parameter: the <see cref="ServiceControlAction"/>.</summary>
    public AsyncRelayCommand ControlCommand { get; }

    public ServiceEntry? SelectedService
    {
        get => _selectedService;
        set
        {
            if (SetProperty(ref _selectedService, value))
                ControlCommand.NotifyCanExecuteChanged();
        }
    }

    protected override async Task<ExplorerResponse> LoadCoreAsync()
    {
        ServiceListResponse response = await Explorer.ListServicesAsync(CancellationToken.None);
        Services.Clear();
        foreach (ServiceEntry service in response.Services.OrderBy(x => x.DisplayName ?? x.Name, StringComparer.CurrentCultureIgnoreCase))
            Services.Add(service);
        return response;
    }

    private Task ControlAsync(object? parameter)
    {
        if (!CanControl || SelectedService is not { } service || parameter is not ServiceControlAction action)
            return Task.CompletedTask;
        return ConfirmAndRunAsync($"{action} service", $"{action} service {service.DisplayName ?? service.Name} ({service.Name}) trên máy B?",
            () => Explorer.ControlServiceAsync(service.Name, action, CancellationToken.None));
    }
}
