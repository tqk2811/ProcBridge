using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// Registry tab: one key at a time (sub keys + values), starting at the roots machine B allows; deleting a value only
/// with <see cref="ExplorerPermissions.RegistryWrite"/>.
/// </summary>
public sealed class RegistryViewModel : ExplorerPageViewModel
{
    private string _path;
    private RegistryValue? _selectedValue;

    public RegistryViewModel(IRemoteExplorer explorer, ExplorerPermissions granted, IUserDialogs dialogs, IReadOnlyList<string> roots)
        : base(explorer, granted, dialogs, ExplorerPermissions.RegistryRead)
    {
        Roots = roots;
        _path = roots.FirstOrDefault() ?? string.Empty;
        OpenSubKeyCommand = new AsyncRelayCommand(p => OpenAsync(p as string));
        UpCommand = new AsyncRelayCommand(UpAsync);
        DeleteValueCommand = new AsyncRelayCommand(DeleteValueAsync, () => CanWrite && SelectedValue is not null);
    }

    /// <summary>Keys machine B allows (from its profile).</summary>
    public IReadOnlyList<string> Roots { get; }

    public ObservableCollection<string> SubKeys { get; } = [];

    public ObservableCollection<RegistryValue> Values { get; } = [];

    public bool CanWrite => Granted.HasFlag(ExplorerPermissions.RegistryWrite);

    public AsyncRelayCommand OpenSubKeyCommand { get; }

    public AsyncRelayCommand UpCommand { get; }

    public AsyncRelayCommand DeleteValueCommand { get; }

    /// <summary>Key shown; editable, Refresh lists it.</summary>
    public string Path
    {
        get => _path;
        set => SetProperty(ref _path, value);
    }

    public RegistryValue? SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (SetProperty(ref _selectedValue, value))
                DeleteValueCommand.NotifyCanExecuteChanged();
        }
    }

    protected override async Task<ExplorerResponse> LoadCoreAsync()
    {
        RegistryListResponse response = await Explorer.ListRegistryAsync(Path, CancellationToken.None);
        SubKeys.Clear();
        Values.Clear();
        if (response.Status == ExplorerStatus.Ok && response.Path is not null)
            Path = response.Path;
        foreach (string subKey in response.SubKeys.Order(StringComparer.OrdinalIgnoreCase))
            SubKeys.Add(subKey);
        foreach (RegistryValue value in response.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            Values.Add(value);
        return response;
    }

    private Task OpenAsync(string? subKey)
    {
        if (string.IsNullOrEmpty(subKey))
            return Task.CompletedTask;
        Path = Path.TrimEnd('\\') + "\\" + subKey;
        return LoadAsync();
    }

    private Task UpAsync()
    {
        int separator = Path.TrimEnd('\\').LastIndexOf('\\');
        if (separator <= 0)
            return Task.CompletedTask;
        Path = Path[..separator];
        return LoadAsync();
    }

    private Task DeleteValueAsync()
    {
        if (!CanWrite || SelectedValue is not { } value)
            return Task.CompletedTask;
        string key = Path;
        string name = value.Name.Length == 0 ? "(Default)" : value.Name;
        return ConfirmAndRunAsync("Xoá giá trị registry", $"Xoá giá trị {name} của {key} trên máy B?",
            () => Explorer.DeleteRegistryValueAsync(key, value.Name, CancellationToken.None));
    }
}
