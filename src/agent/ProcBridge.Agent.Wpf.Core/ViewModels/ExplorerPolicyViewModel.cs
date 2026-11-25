using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Mvvm;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>
/// Editor of the Remote Explorer profile of one paired machine A (plan section 14): file roots, registry roots and one
/// check box per permission, all off for a machine without a profile. <see cref="Save"/> writes it to the store; it
/// applies to the machine's next connections.
/// </summary>
public sealed class ExplorerPolicyViewModel : ObservableObject
{
    private static readonly string[] _registryHives =
    [
        "HKLM", "HKEY_LOCAL_MACHINE", "HKCU", "HKEY_CURRENT_USER", "HKCR", "HKEY_CLASSES_ROOT",
        "HKU", "HKEY_USERS", "HKCC", "HKEY_CURRENT_CONFIG",
    ];

    private readonly ExplorerPolicyStore _store;
    private readonly IDialogService _dialogs;
    private string? _selectedFileRoot;
    private string? _selectedRegistryRoot;
    private string _newRegistryRoot = string.Empty;
    private string? _errorText;

    public ExplorerPolicyViewModel(string fingerprint, string machineName, ExplorerPolicyStore store, IDialogService dialogs)
    {
        Fingerprint = fingerprint;
        MachineName = machineName;
        _store = store;
        _dialogs = dialogs;
        ExplorerPolicy policy = store.Get(fingerprint);
        FileRoots = new ObservableCollection<string>(policy.FileRoots);
        RegistryRoots = new ObservableCollection<string>(policy.RegistryRoots);
        Permissions = ExplorerPermissionLabels.Flags.Select(x => new PermissionOptionViewModel(x, policy.Permissions.HasFlag(x))).ToList();
        AddFileRootCommand = new RelayCommand(AddFileRoot);
        RemoveFileRootCommand = new RelayCommand(() => FileRoots.Remove(SelectedFileRoot!), () => SelectedFileRoot is not null);
        AddRegistryRootCommand = new RelayCommand(() => AddRegistryRoot(NewRegistryRoot));
        RemoveRegistryRootCommand = new RelayCommand(() => RegistryRoots.Remove(SelectedRegistryRoot!), () => SelectedRegistryRoot is not null);
        SaveCommand = new RelayCommand(() => Save());
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke(false));
    }

    /// <summary>Asks the window to close; true when the profile was saved.</summary>
    public event Action<bool>? CloseRequested;

    public string Fingerprint { get; }

    public string MachineName { get; }

    public ObservableCollection<string> FileRoots { get; }

    public ObservableCollection<string> RegistryRoots { get; }

    public IReadOnlyList<PermissionOptionViewModel> Permissions { get; }

    public string? SelectedFileRoot
    {
        get => _selectedFileRoot;
        set
        {
            if (SetProperty(ref _selectedFileRoot, value))
                RemoveFileRootCommand.RaiseCanExecuteChanged();
        }
    }

    public string? SelectedRegistryRoot
    {
        get => _selectedRegistryRoot;
        set
        {
            if (SetProperty(ref _selectedRegistryRoot, value))
                RemoveRegistryRootCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Registry key typed by the user, e.g. <c>HKCU\Software\Vendor</c>.</summary>
    public string NewRegistryRoot
    {
        get => _newRegistryRoot;
        set => SetProperty(ref _newRegistryRoot, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public RelayCommand AddFileRootCommand { get; }

    public RelayCommand RemoveFileRootCommand { get; }

    public RelayCommand AddRegistryRootCommand { get; }

    public RelayCommand RemoveRegistryRootCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>The profile the editor currently describes.</summary>
    public ExplorerPolicy BuildPolicy() => new()
    {
        FileRoots = FileRoots.ToArray(),
        RegistryRoots = RegistryRoots.ToArray(),
        Permissions = Permissions.Where(x => x.IsChecked).Aggregate(ExplorerPermissions.None, (all, x) => all | x.Flag),
    };

    private void AddFileRoot()
    {
        string? folder = _dialogs.PickFolder($"Thư mục máy {MachineName} được duyệt");
        if (folder is not null)
            AddFileRoot(folder);
    }

    /// <summary>Adds a folder (full path) to the file roots; false with <see cref="ErrorText"/> when it is not valid.</summary>
    public bool AddFileRoot(string folder)
    {
        if (!Path.IsPathFullyQualified(folder))
        {
            ErrorText = $"'{folder}' không phải đường dẫn đầy đủ.";
            return false;
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // Keep a drive root as "C:\", not "C:" (which means the current folder of drive C).
        if (full.Length == 2 && full[1] == ':')
            full += Path.DirectorySeparatorChar;
        if (!FileRoots.Contains(full, StringComparer.OrdinalIgnoreCase))
            FileRoots.Add(full);
        ErrorText = null;
        return true;
    }

    /// <summary>Adds a registry key to the registry roots; false with <see cref="ErrorText"/> when its hive is unknown.</summary>
    public bool AddRegistryRoot(string key)
    {
        string trimmed = key.Trim().TrimEnd('\\');
        string hive = trimmed.Split('\\', 2)[0];
        if (trimmed.Length == 0 || !_registryHives.Contains(hive, StringComparer.OrdinalIgnoreCase))
        {
            ErrorText = $"'{key}' phải bắt đầu bằng một hive: HKLM, HKCU, HKCR, HKU hoặc HKCC.";
            return false;
        }
        if (!RegistryRoots.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            RegistryRoots.Add(trimmed);
        NewRegistryRoot = string.Empty;
        ErrorText = null;
        return true;
    }

    /// <summary>Writes the profile to the store and asks the window to close.</summary>
    public void Save()
    {
        _store.Set(Fingerprint, BuildPolicy());
        CloseRequested?.Invoke(true);
    }
}
