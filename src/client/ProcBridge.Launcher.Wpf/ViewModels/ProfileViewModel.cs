using System.Collections.ObjectModel;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// Editor of one run profile. Folders of machine B (share root, mapping targets) are picked with the Remote Explorer
/// of the profile's machine; folders of machine A with the local folder dialog.
/// </summary>
public sealed class ProfileViewModel : ObservableObject
{
    private readonly IUserDialogs _dialogs;
    private readonly Func<string, PairedMachine?> _findMachine;
    private string _name;
    private string _machineFingerprint;
    private string _shareName;
    private string _shareRoot;
    private string _program;
    private string _arguments;
    private string _workingDirectory;
    private bool _disableBuildServers;
    private bool _writeThrough;
    private bool _overwriteOnConflict;
    private string _architecture;
    private FolderMappingViewModel? _selectedMapping;

    public ProfileViewModel(LaunchProfile profile, IUserDialogs dialogs, Func<string, PairedMachine?> findMachine)
    {
        _dialogs = dialogs;
        _findMachine = findMachine;
        _name = profile.Name;
        _machineFingerprint = profile.MachineFingerprint;
        _shareName = profile.ShareName;
        _shareRoot = profile.ShareRoot;
        _program = profile.Program;
        _arguments = profile.Arguments;
        _workingDirectory = profile.WorkingDirectory;
        _disableBuildServers = profile.DisableBuildServers;
        _writeThrough = profile.WriteThrough;
        _overwriteOnConflict = profile.OverwriteOnConflict;
        _architecture = profile.Architecture;
        foreach (FolderMapping mapping in profile.Mappings)
            Mappings.Add(new FolderMappingViewModel(mapping));

        AddMappingCommand = new RelayCommand(() => SelectedMapping = Add(new FolderMappingViewModel(new FolderMapping())));
        RemoveMappingCommand = new RelayCommand(p => { if (p is FolderMappingViewModel m) Mappings.Remove(m); });
        PickVirtualRootCommand = new RelayCommand(PickVirtualRoot);
        PickRemotePathCommand = new RelayCommand(PickRemotePath);
        PickShareRootCommand = new RelayCommand(PickShareRoot);
        PickWorkingDirectoryCommand = new RelayCommand(() => WorkingDirectory = _dialogs.PickLocalFolder(WorkingDirectory) ?? WorkingDirectory);
    }

    public static IReadOnlyList<string> Architectures { get; } = ["x64", "x86"];

    public ObservableCollection<FolderMappingViewModel> Mappings { get; } = [];

    public RelayCommand AddMappingCommand { get; }

    public RelayCommand RemoveMappingCommand { get; }

    public RelayCommand PickVirtualRootCommand { get; }

    public RelayCommand PickRemotePathCommand { get; }

    public RelayCommand PickShareRootCommand { get; }

    public RelayCommand PickWorkingDirectoryCommand { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string MachineFingerprint
    {
        get => _machineFingerprint;
        set => SetProperty(ref _machineFingerprint, value ?? string.Empty);
    }

    public string ShareName
    {
        get => _shareName;
        set => SetProperty(ref _shareName, value);
    }

    public string ShareRoot
    {
        get => _shareRoot;
        set => SetProperty(ref _shareRoot, value);
    }

    public string Program
    {
        get => _program;
        set => SetProperty(ref _program, value);
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool DisableBuildServers
    {
        get => _disableBuildServers;
        set => SetProperty(ref _disableBuildServers, value);
    }

    public bool WriteThrough
    {
        get => _writeThrough;
        set => SetProperty(ref _writeThrough, value);
    }

    public bool OverwriteOnConflict
    {
        get => _overwriteOnConflict;
        set => SetProperty(ref _overwriteOnConflict, value);
    }

    public string Architecture
    {
        get => _architecture;
        set => SetProperty(ref _architecture, value);
    }

    public FolderMappingViewModel? SelectedMapping
    {
        get => _selectedMapping;
        set => SetProperty(ref _selectedMapping, value);
    }

    public PairedMachine? Machine => _findMachine(MachineFingerprint);

    public LaunchProfile ToModel() => new()
    {
        Name = Name.Trim(),
        MachineFingerprint = MachineFingerprint,
        ShareName = ShareName.Trim(),
        ShareRoot = ShareRoot.Trim(),
        Mappings = Mappings.Select(x => x.ToModel()).ToArray(),
        Program = Program.Trim(),
        Arguments = Arguments.Trim(),
        WorkingDirectory = WorkingDirectory.Trim(),
        DisableBuildServers = DisableBuildServers,
        WriteThrough = WriteThrough,
        OverwriteOnConflict = OverwriteOnConflict,
        Architecture = Architecture,
    };

    private FolderMappingViewModel Add(FolderMappingViewModel mapping)
    {
        Mappings.Add(mapping);
        return mapping;
    }

    private void PickVirtualRoot(object? parameter)
    {
        if (parameter is FolderMappingViewModel mapping && _dialogs.PickLocalFolder(mapping.VirtualRoot) is { } path)
            mapping.VirtualRoot = path;
    }

    private void PickRemotePath(object? parameter)
    {
        if (parameter is not FolderMappingViewModel mapping || RequireMachine() is not { } machine)
            return;
        if (_dialogs.PickRemoteFolder(machine, string.IsNullOrEmpty(mapping.RemotePath) ? ShareRoot : mapping.RemotePath) is { } path)
        {
            mapping.RemotePath = path;
            // First mapping and no share root yet: the picked folder is the natural root.
            if (string.IsNullOrWhiteSpace(ShareRoot))
                ShareRoot = path;
        }
    }

    private void PickShareRoot()
    {
        if (RequireMachine() is { } machine && _dialogs.PickRemoteFolder(machine, ShareRoot) is { } path)
            ShareRoot = path;
    }

    private PairedMachine? RequireMachine()
    {
        PairedMachine? machine = Machine;
        if (machine is null)
            _dialogs.ShowError("Chưa chọn máy B", "Chọn máy B đã ghép cặp cho hồ sơ trước khi duyệt thư mục của nó.");
        return machine;
    }
}
