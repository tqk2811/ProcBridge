using System.Collections.ObjectModel;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Paired machines B: list, revoke, and the pairing of a new one.</summary>
public sealed class MachinesViewModel : ObservableObject
{
    private readonly IPairedMachineRegistry _registry;
    private readonly IUserDialogs _dialogs;
    private PairedMachine? _selectedMachine;

    public MachinesViewModel(IPairedMachineRegistry registry, IUserDialogs dialogs)
    {
        _registry = registry;
        _dialogs = dialogs;
        Pairing = new PairingViewModel(registry);
        Pairing.Paired += _ => Refresh();
        RefreshCommand = new RelayCommand(Refresh);
        RevokeCommand = new RelayCommand(Revoke, () => SelectedMachine is not null);
        Refresh();
    }

    public PairingViewModel Pairing { get; }

    public string OwnFingerprint => _registry.OwnFingerprint;

    public ObservableCollection<PairedMachine> Machines { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RevokeCommand { get; }

    public PairedMachine? SelectedMachine
    {
        get => _selectedMachine;
        set
        {
            if (SetProperty(ref _selectedMachine, value))
                RevokeCommand.NotifyCanExecuteChanged();
        }
    }

    public PairedMachine? Find(string fingerprint) => Machines.FirstOrDefault(x => x.Fingerprint == fingerprint);

    public void Refresh()
    {
        string? selected = SelectedMachine?.Fingerprint;
        Machines.Clear();
        foreach (PairedMachine machine in _registry.List().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            Machines.Add(machine);
        SelectedMachine = selected is null ? null : Find(selected);
    }

    private void Revoke()
    {
        if (SelectedMachine is not { } machine)
            return;
        if (!_dialogs.Confirm("Thu hồi ghép cặp", $"Thu hồi {machine.Name} ({machine.Fingerprint})? Máy này sẽ không kết nối được tới máy B đó cho tới khi ghép cặp lại."))
            return;
        _registry.Revoke(machine.Fingerprint);
        Refresh();
    }
}
