using System.Collections.ObjectModel;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>Saved run profiles: list, add, remove, save, and Run/Stop of the selected one.</summary>
public sealed class ProfilesViewModel : ObservableObject
{
    private readonly IProfileStore _store;
    private readonly IUserDialogs _dialogs;
    private readonly MachinesViewModel _machines;
    private readonly IRemoteExplorerFactory? _explorers;
    private ProfileViewModel? _selectedProfile;
    private string? _statusText;

    /// <param name="explorers">Lets each profile list the shares of its machine B; null in tests that do not need it.</param>
    public ProfilesViewModel(IProfileStore store, IUserDialogs dialogs, MachinesViewModel machines, RunViewModel run, IRemoteExplorerFactory? explorers = null)
    {
        _store = store;
        _explorers = explorers;
        _dialogs = dialogs;
        _machines = machines;
        Run = run;
        AddCommand = new RelayCommand(Add);
        RemoveCommand = new RelayCommand(Remove, () => SelectedProfile is not null);
        SaveCommand = new RelayCommand(() => Save());
        RunCommand = new AsyncRelayCommand(RunSelectedAsync, () => SelectedProfile is not null && !Run.IsRunning);
        Run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RunViewModel.IsRunning))
                RunCommand.NotifyCanExecuteChanged();
        };
        Load();
    }

    public RunViewModel Run { get; }

    public MachinesViewModel Machines => _machines;

    public ObservableCollection<ProfileViewModel> Profiles { get; } = [];

    public RelayCommand AddCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand SaveCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public ProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                RemoveCommand.NotifyCanExecuteChanged();
                RunCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Writes every profile; false (and a message) when the file cannot be written.</summary>
    public bool Save()
    {
        try
        {
            _store.Save(Profiles.Select(x => x.ToModel()).ToArray());
            StatusText = $"Đã lưu {Profiles.Count} hồ sơ.";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Không lưu được hồ sơ: {e.Message}";
            return false;
        }
    }

    private void Load()
    {
        IReadOnlyList<LaunchProfile> profiles;
        try
        {
            profiles = _store.Load();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = $"Không đọc được hồ sơ đã lưu: {e.Message}";
            profiles = [];
        }
        foreach (LaunchProfile profile in profiles)
            Profiles.Add(Create(profile));
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private ProfileViewModel Create(LaunchProfile profile) => new(profile, _dialogs, _machines.Find, _explorers);

    private void Add()
    {
        var profile = Create(new LaunchProfile
        {
            Name = $"Hồ sơ {Profiles.Count + 1}",
            MachineFingerprint = _machines.SelectedMachine?.Fingerprint ?? _machines.Machines.FirstOrDefault()?.Fingerprint ?? string.Empty,
        });
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    private void Remove()
    {
        if (SelectedProfile is not { } profile || !_dialogs.Confirm("Xoá hồ sơ", $"Xoá hồ sơ '{profile.Name}'?"))
            return;
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private Task RunSelectedAsync()
    {
        if (SelectedProfile is not { } profile)
            return Task.CompletedTask;
        Save();
        return Run.RunAsync(profile.ToModel(), profile.Machine);
    }
}
