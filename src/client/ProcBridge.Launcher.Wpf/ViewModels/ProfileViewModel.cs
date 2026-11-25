using System.Collections.ObjectModel;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Helpers;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Models;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Transport.Relay;
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
    private readonly IRemoteExplorerFactory? _explorers;
    private string? _shareStatusText;
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
    private bool _useRelay;
    private string _relayUrl;
    private FolderMappingViewModel? _selectedMapping;

    /// <param name="explorers">Lists the shares of machine B (<see cref="LoadSharesCommand"/>); null hides that list.</param>
    public ProfileViewModel(LaunchProfile profile, IUserDialogs dialogs, Func<string, PairedMachine?> findMachine, IRemoteExplorerFactory? explorers = null)
    {
        _dialogs = dialogs;
        _findMachine = findMachine;
        _explorers = explorers;
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
        _useRelay = profile.UseRelay;
        _relayUrl = profile.RelayUrl;
        foreach (FolderMapping mapping in profile.Mappings)
            Mappings.Add(new FolderMappingViewModel(mapping));

        AddMappingCommand = new RelayCommand(() => SelectedMapping = Add(new FolderMappingViewModel(new FolderMapping())));
        RemoveMappingCommand = new RelayCommand(p => { if (p is FolderMappingViewModel m) Mappings.Remove(m); });
        PickVirtualRootCommand = new RelayCommand(PickVirtualRoot);
        PickRemotePathCommand = new RelayCommand(PickRemotePath);
        PickShareRootCommand = new RelayCommand(PickShareRoot);
        PickWorkingDirectoryCommand = new RelayCommand(() => WorkingDirectory = _dialogs.PickLocalFolder(WorkingDirectory) ?? WorkingDirectory);
        LoadSharesCommand = new AsyncRelayCommand(LoadSharesAsync, () => _explorers is not null);
    }

    /// <summary>Shares machine B serves (name and folder), filled by <see cref="LoadSharesCommand"/>.</summary>
    public ObservableCollection<ShareEntry> Shares { get; } = [];

    /// <summary>Asks machine B for its shares, so the share is picked instead of typed with its folder.</summary>
    public AsyncRelayCommand LoadSharesCommand { get; }

    /// <summary>Result of the last <see cref="LoadSharesCommand"/>; null before.</summary>
    public string? ShareStatusText
    {
        get => _shareStatusText;
        private set => SetProperty(ref _shareStatusText, value);
    }

    /// <summary>The entry of <see cref="Shares"/> named <see cref="ShareName"/>; setting it fills the name and the folder.</summary>
    public ShareEntry? SelectedShare
    {
        get => Shares.FirstOrDefault(x => string.Equals(x.Name, ShareName.Trim(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is null)
                return;
            ShareName = value.Name;
            UseRootOf(value);
        }
    }

    /// <summary>The folder of <paramref name="share"/>, when machine B gave it (its Explorer profile may hide it).</summary>
    private void UseRootOf(ShareEntry share)
    {
        if (share.RootPath.Length > 0)
            ShareRoot = share.RootPath;
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
        set
        {
            if (!SetProperty(ref _machineFingerprint, value ?? string.Empty))
                return;
            // The shares listed belong to the previous machine.
            Shares.Clear();
            ShareStatusText = null;
            OnPropertyChanged(nameof(SelectedShare));
            // Only on a change by the user: loading a saved profile must not edit it.
            ApplyRelayOfMachine();
        }
    }

    public string ShareName
    {
        get => _shareName;
        set
        {
            if (!SetProperty(ref _shareName, value ?? string.Empty))
                return;
            OnPropertyChanged(nameof(SelectedShare));
            // A share machine B reported: its folder is known, the user does not type it.
            if (SelectedShare is { } share)
                UseRootOf(share);
        }
    }

    public string ShareRoot
    {
        get => _shareRoot;
        set => SetProperty(ref _shareRoot, value ?? string.Empty);
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

    /// <summary>Reach machine B through a relay (internet) instead of its LAN address.</summary>
    public bool UseRelay
    {
        get => _useRelay;
        set => SetProperty(ref _useRelay, value);
    }

    public string RelayUrl
    {
        get => _relayUrl;
        set => SetProperty(ref _relayUrl, value);
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
        UseRelay = UseRelay,
        RelayUrl = RelayUrl.Trim(),
    };

    /// <summary>
    /// A machine paired through the relay (address <c>relay &lt;url&gt;</c>) has no LAN address: turn "Kết nối qua relay" on
    /// with that URL, unless the profile already goes through a relay of its own.
    /// </summary>
    private void ApplyRelayOfMachine()
    {
        if (RelayPairingConnector.TryParseRelayAddress(Machine?.Address) is not { } relayUrl)
            return;
        if (UseRelay && !string.IsNullOrWhiteSpace(RelayUrl))
            return;
        UseRelay = true;
        RelayUrl = relayUrl.ToString();
    }

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
        if (_dialogs.PickRemoteFolder(machine, string.IsNullOrEmpty(mapping.RemotePath) ? ShareRoot : mapping.RemotePath) is not { } path)
            return;
        if (string.IsNullOrWhiteSpace(ShareRoot))
        {
            // No share yet: the share of machine B holding the folder, else the folder itself as the root.
            ShareEntry? holder = Shares
                .Where(x => x.RootPath.Length > 0 && RemotePathHelper.ToShareRelative(x.RootPath, path) is not null)
                .MaxBy(x => x.RootPath.Length);
            if (holder is not null)
                SelectedShare = holder;
            else if (!Shares.Any(x => x.RootPath.Length > 0))
                ShareRoot = path;
        }
        if (RemotePathHelper.ToShareRelative(ShareRoot, path) is null)
        {
            // Checked now rather than at run time, where a wrong root only shows as the wrong folder being read.
            _dialogs.ShowError("Thư mục nằm ngoài chia sẻ",
                string.IsNullOrWhiteSpace(ShareRoot)
                    ? $"Thư mục '{path}' không nằm trong chia sẻ nào của máy B. Chọn thư mục trong một chia sẻ."
                    : $"Thư mục '{path}' không nằm trong chia sẻ '{ShareName}' ({ShareRoot}) của máy B. Chọn thư mục trong chia sẻ đó, hoặc đổi chia sẻ.");
            return;
        }
        mapping.RemotePath = path;
    }

    /// <summary>Fills <see cref="Shares"/> from the profile's machine; never throws.</summary>
    public async Task LoadSharesAsync()
    {
        if (_explorers is null || RequireMachine() is not { } machine)
            return;
        ShareStatusText = $"Đang lấy danh sách chia sẻ của {machine.Name}…";
        string fingerprint = MachineFingerprint;
        IRemoteExplorer explorer = _explorers.Create(machine);
        try
        {
            ShareListResponse response = await explorer.ListSharesAsync(CancellationToken.None);
            // The user picked another machine meanwhile: this list is not its.
            if (MachineFingerprint != fingerprint)
                return;
            Shares.Clear();
            foreach (ShareEntry share in response.Shares.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                Shares.Add(share);
            OnPropertyChanged(nameof(SelectedShare));
            if (SelectedShare is { } current)
                UseRootOf(current);
            else if (Shares.Count == 1 && string.IsNullOrWhiteSpace(ShareName))
                SelectedShare = Shares[0];
            ShareStatusText = Shares.Count == 0
                ? $"{machine.Name} chưa chia sẻ thư mục nào."
                : SelectedShare is null && !string.IsNullOrWhiteSpace(ShareName)
                    ? $"{machine.Name} không có chia sẻ '{ShareName}'. Chọn một trong {Shares.Count} chia sẻ."
                    : $"{machine.Name} có {Shares.Count} chia sẻ.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (MachineFingerprint == fingerprint)
                ShareStatusText = $"Không lấy được danh sách chia sẻ ({e.Message}). Nhập tên chia sẻ và chọn thư mục gốc bằng tay.";
        }
        finally
        {
            await explorer.DisposeAsync();
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
