using System.Collections.ObjectModel;
using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Agent.Wpf.Core.Mvvm;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>
/// Main window of the Agent app (machine B): server start/stop, shares, pairing, paired machines and their Explorer
/// profiles. Created on the UI thread; events of the host arrive on other threads and are posted back to it through the
/// <see cref="SynchronizationContext"/> captured at construction (none in tests: they run inline). The stores and the
/// host are borrowed.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IAgentHost _host;
    private readonly PairedMachineStore _pairedMachines;
    private readonly ExplorerPolicyStore _explorerPolicies;
    private readonly IAgentSettingsStore _settingsStore;
    private readonly IDialogService _dialogs;
    private readonly Func<DateTime> _utcNow;
    private readonly Random? _random;
    private readonly SynchronizationContext? _ui;
    private readonly AgentUiSettings _settings;
    private HashSet<string> _connectedFingerprints = new(StringComparer.Ordinal);
    private string _port;
    private string _bindAddress;
    private string _relayUrl;
    private string? _relayState;
    private bool _isRunning;
    private bool _sharesChangedWhileRunning;
    private bool _pairingOpen;
    private string _statusText = "Đã dừng";
    private string? _errorText;
    private ShareItemViewModel? _selectedShare;
    private PairedMachineItemViewModel? _selectedPairedMachine;

    /// <param name="isElevated">The app runs as administrator (plan section 15, question 4).</param>
    /// <param name="utcNow">Clock for "last seen"; null uses <see cref="DateTime.UtcNow"/>.</param>
    /// <param name="random">Given to each <see cref="PairingViewModel"/> (tests).</param>
    public MainViewModel(IAgentHost host, PairedMachineStore pairedMachines, ExplorerPolicyStore explorerPolicies, IAgentSettingsStore settingsStore,
        IDialogService dialogs, bool isElevated, Func<DateTime>? utcNow = null, Random? random = null)
    {
        _host = host;
        _pairedMachines = pairedMachines;
        _explorerPolicies = explorerPolicies;
        _settingsStore = settingsStore;
        _dialogs = dialogs;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _random = random;
        _ui = SynchronizationContext.Current;
        IsElevated = isElevated;
        _settings = settingsStore.Load();
        _port = _settings.Port.ToString();
        _bindAddress = _settings.BindAddress;
        _relayUrl = _settings.RelayUrl;
        foreach (ShareSettings share in _settings.Shares)
            AddShareItem(new ShareItemViewModel(share));

        StartCommand = new RelayCommand(Start, () => !IsRunning);
        StopCommand = new AsyncRelayCommand(StopAsync, ShowError, () => IsRunning);
        AddShareCommand = new RelayCommand(AddShare);
        RemoveShareCommand = new RelayCommand(RemoveSelectedShare, () => SelectedShare is not null);
        SaveCommand = new RelayCommand(Save);
        RevokeCommand = new RelayCommand(x => Revoke(x as PairedMachineItemViewModel ?? SelectedPairedMachine), x => (x ?? SelectedPairedMachine) is PairedMachineItemViewModel);
        EditExplorerPolicyCommand = new RelayCommand(x => EditExplorerPolicy(x as PairedMachineItemViewModel ?? SelectedPairedMachine),
            x => (x ?? SelectedPairedMachine) is PairedMachineItemViewModel);

        _host.PairingRequested += OnPairingRequested;
        _pairedMachines.Revoked += OnRevoked;
        ReloadPairedMachines();
    }

    public string Fingerprint => _host.Fingerprint;

    public bool IsElevated { get; }

    public string ElevationText => IsElevated
        ? "Đang chạy với quyền admin: máy A có thể làm mọi việc tài khoản admin làm được trong phạm vi hồ sơ quyền."
        : "Đang chạy với quyền người dùng thường. Cần quyền cao hơn thì mở Agent bằng Run as administrator.";

    public string Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public string BindAddress
    {
        get => _bindAddress;
        set => SetProperty(ref _bindAddress, value);
    }

    /// <summary>Relay URL (internet access, plan 8.2); empty for the LAN only.</summary>
    public string RelayUrl
    {
        get => _relayUrl;
        set => SetProperty(ref _relayUrl, value);
    }

    /// <summary>Relay link state while running (null without relay).</summary>
    public string? RelayState
    {
        get => _relayState;
        private set => SetProperty(ref _relayState, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>Accept pairing requests now (turned off again once a machine was paired).</summary>
    public bool AllowPairing
    {
        get => _host.AllowPairing;
        set
        {
            if (_host.AllowPairing == value)
                return;
            _host.AllowPairing = value;
            OnPropertyChanged();
        }
    }

    public bool StartServerOnLaunch
    {
        get => _settings.StartServerOnLaunch;
        set
        {
            if (_settings.StartServerOnLaunch == value)
                return;
            _settings.StartServerOnLaunch = value;
            OnPropertyChanged();
            Save();
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value)
                return;
            _settings.MinimizeToTray = value;
            OnPropertyChanged();
            Save();
        }
    }

    /// <summary>Shares were edited while running: they apply after a restart of the server.</summary>
    public bool SharesNeedRestart
    {
        get => _sharesChangedWhileRunning;
        private set => SetProperty(ref _sharesChangedWhileRunning, value);
    }

    public ObservableCollection<ShareItemViewModel> Shares { get; } = [];

    public ObservableCollection<ConnectedMachineViewModel> Connections { get; } = [];

    public ObservableCollection<PairedMachineItemViewModel> PairedMachines { get; } = [];

    public ShareItemViewModel? SelectedShare
    {
        get => _selectedShare;
        set
        {
            if (SetProperty(ref _selectedShare, value))
                RemoveShareCommand.RaiseCanExecuteChanged();
        }
    }

    public PairedMachineItemViewModel? SelectedPairedMachine
    {
        get => _selectedPairedMachine;
        set
        {
            if (SetProperty(ref _selectedPairedMachine, value))
            {
                RevokeCommand.RaiseCanExecuteChanged();
                EditExplorerPolicyCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public RelayCommand AddShareCommand { get; }

    public RelayCommand RemoveShareCommand { get; }

    public RelayCommand SaveCommand { get; }

    /// <summary>Parameter: the <see cref="PairedMachineItemViewModel"/>, or none for the selected one.</summary>
    public RelayCommand RevokeCommand { get; }

    /// <summary>Parameter: the <see cref="PairedMachineItemViewModel"/>, or none for the selected one.</summary>
    public RelayCommand EditExplorerPolicyCommand { get; }

    /// <summary>Validates the settings, saves them and starts the server; errors go to <see cref="ErrorText"/>.</summary>
    public void Start()
    {
        if (IsRunning)
            return;
        if (!TryReadEndpoint(out IPAddress? address, out int port) || !TryReadShares(out List<ShareSettings>? shares) || !TryReadRelay(out RelayEndpoint? relay))
            return;
        Save();
        try
        {
            _host.Start(shares, address, port, relay);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or System.Net.Sockets.SocketException or ArgumentException or IOException)
        {
            ShowError(e);
            return;
        }
        ErrorText = null;
        SharesNeedRestart = false;
        IsRunning = true;
        StatusText = $"Đang chạy trên {address}:{_host.Port}{(relay is null ? string.Empty : $" và qua relay {relay.BaseUrl.Host}")}, mã hoá đầu cuối, {shares.Count} thư mục chia sẻ";
        RelayState = _host.RelayState;
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;
        await _host.StopAsync();
        IsRunning = false;
        StatusText = "Đã dừng";
        RelayState = null;
        Refresh();
    }

    /// <summary>Saves the editable settings (port, address, shares, options, last seen).</summary>
    public void Save()
    {
        if (int.TryParse(Port, out int port))
            _settings.Port = port;
        _settings.BindAddress = BindAddress.Trim();
        _settings.Shares = Shares.Select(x => x.ToSettings()).ToList();
        _settings.RelayUrl = RelayUrl.Trim();
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowError(e);
        }
    }

    /// <summary>
    /// Updates the connected machines and the "last seen" of paired machines; the window calls it on a timer.
    /// Saves the settings when the set of connected machines changed.
    /// </summary>
    public void Refresh()
    {
        RelayState = _host.RelayState;
        IReadOnlyList<AgentConnectionInfo> connections = _host.GetConnections();
        DateTime now = _utcNow();
        var grouped = connections
            .GroupBy(x => x.PeerFingerprint ?? string.Empty)
            .Select(g => new ConnectedMachineViewModel
            {
                Fingerprint = g.Key,
                Name = g.Key.Length == 0 ? "(đang bắt tay)" : _pairedMachines.Find(g.Key)?.Name ?? "(chưa rõ)",
                RemoteAddresses = string.Join(", ", g.Select(x => HostOf(x.RemoteAddress)).Distinct()),
                ConnectionCount = g.Count(),
                ConnectedSinceLocal = g.Min(x => x.ConnectedAtUtc).ToLocalTime(),
            })
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (!grouped.SequenceEqual(Connections))
        {
            Connections.Clear();
            foreach (ConnectedMachineViewModel machine in grouped)
                Connections.Add(machine);
        }

        var connected = new HashSet<string>(grouped.Where(x => x.Fingerprint.Length > 0).Select(x => x.Fingerprint), StringComparer.Ordinal);
        foreach (PairedMachineItemViewModel machine in PairedMachines)
        {
            int count = grouped.FirstOrDefault(x => x.Fingerprint == machine.Fingerprint)?.ConnectionCount ?? 0;
            machine.ConnectionCount = count;
            if (count > 0)
            {
                machine.LastSeenUtc = now;
                _settings.LastSeenUtc[machine.Fingerprint] = now;
            }
        }
        if (!connected.SetEquals(_connectedFingerprints))
        {
            _connectedFingerprints = connected;
            Save();
        }
    }

    /// <summary>Asks, then revokes the machine: its connections close, its Explorer profile is dropped.</summary>
    public void Revoke(PairedMachineItemViewModel? machine)
    {
        if (machine is null)
            return;
        if (!_dialogs.Confirm("Thu hồi máy đã ghép cặp",
                $"Thu hồi '{machine.Name}' ({machine.Fingerprint})?\nMọi kết nối của máy này bị đóng ngay, hồ sơ quyền Explorer bị xoá, muốn dùng lại phải ghép cặp lại."))
            return;
        _pairedMachines.Revoke(machine.Fingerprint);
        _explorerPolicies.Remove(machine.Fingerprint);
        _settings.LastSeenUtc.Remove(machine.Fingerprint);
        Save();
        ReloadPairedMachines();
    }

    public void EditExplorerPolicy(PairedMachineItemViewModel? machine)
    {
        if (machine is null)
            return;
        _dialogs.EditExplorerPolicy(new ExplorerPolicyViewModel(machine.Fingerprint, machine.Name, _explorerPolicies, _dialogs));
    }

    /// <summary>Stops the server and saves; called when the app exits.</summary>
    public async Task ShutdownAsync()
    {
        _host.PairingRequested -= OnPairingRequested;
        _pairedMachines.Revoked -= OnRevoked;
        Save();
        await _host.StopAsync();
        IsRunning = false;
    }

    private void OnPairingRequested(IPairingPrompt prompt) => Post(() => ShowPairing(prompt));

    private void ShowPairing(IPairingPrompt prompt)
    {
        // One dialog at a time: a second request while the user is deciding is refused.
        if (_pairingOpen)
        {
            prompt.Reject();
            return;
        }
        _pairingOpen = true;
        try
        {
            var viewModel = new PairingViewModel(prompt, _random);
            _dialogs.ShowPairing(viewModel);
            if (!viewModel.IsDecided)
                viewModel.Reject();
        }
        finally
        {
            _pairingOpen = false;
        }
        _ = prompt.Completion.ContinueWith(task => Post(() => OnPairingCompleted(prompt, task.IsCompletedSuccessfully && task.Result)), TaskScheduler.Default);
    }

    private void OnPairingCompleted(IPairingPrompt prompt, bool paired)
    {
        if (paired)
        {
            AllowPairing = false;
            ReloadPairedMachines();
            StatusText = $"Đã ghép cặp '{prompt.PeerName}'. Máy này chưa có quyền Explorer nào cho tới khi được đặt hồ sơ quyền.";
        }
        else
        {
            StatusText = $"Ghép cặp với '{prompt.PeerName}' không thành công (bị từ chối, sai mã hoặc hết giờ).";
        }
    }

    private void OnRevoked(PairedMachine machine) => Post(ReloadPairedMachines);

    private void ReloadPairedMachines()
    {
        string? selected = SelectedPairedMachine?.Fingerprint;
        PairedMachines.Clear();
        foreach (PairedMachine machine in _pairedMachines.List().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var item = new PairedMachineItemViewModel(machine);
            if (_settings.LastSeenUtc.TryGetValue(machine.Fingerprint, out DateTime seen))
                item.LastSeenUtc = seen;
            PairedMachines.Add(item);
        }
        SelectedPairedMachine = PairedMachines.FirstOrDefault(x => x.Fingerprint == selected);
    }

    private void AddShare()
    {
        string? folder = _dialogs.PickFolder("Thư mục chia sẻ cho máy A");
        if (folder is null)
            return;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (string.IsNullOrEmpty(name))
            name = "share";
        string unique = name;
        for (int i = 2; Shares.Any(x => string.Equals(x.Name, unique, StringComparison.OrdinalIgnoreCase)); i++)
            unique = $"{name}{i}";
        var item = new ShareItemViewModel(new ShareSettings { Name = unique, Path = folder });
        AddShareItem(item);
        SelectedShare = item;
        OnSharesChanged();
    }

    private void RemoveSelectedShare()
    {
        if (SelectedShare is null)
            return;
        SelectedShare.Changed -= OnSharesChanged;
        Shares.Remove(SelectedShare);
        SelectedShare = null;
        OnSharesChanged();
    }

    private void AddShareItem(ShareItemViewModel item)
    {
        item.Changed += OnSharesChanged;
        Shares.Add(item);
    }

    private void OnSharesChanged()
    {
        if (IsRunning)
            SharesNeedRestart = true;
    }

    private bool TryReadEndpoint(out IPAddress address, out int port)
    {
        address = IPAddress.Any;
        if (!int.TryParse(Port, out port) || port is < 1 or > 65535)
        {
            ErrorText = "Cổng phải là số từ 1 đến 65535.";
            return false;
        }
        if (!IPAddress.TryParse(BindAddress.Trim(), out IPAddress? parsed))
        {
            ErrorText = $"'{BindAddress}' không phải địa chỉ IP (vd 0.0.0.0 để nghe mọi card mạng).";
            return false;
        }
        address = parsed;
        return true;
    }

    /// <summary>Relay from the form: none when the URL is empty. No token: the Agent proves its HostId with its key.</summary>
    private bool TryReadRelay(out RelayEndpoint? relay)
    {
        relay = null;
        string url = RelayUrl.Trim();
        if (url.Length == 0)
            return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("https" or "http"))
        {
            ErrorText = $"'{url}' không phải địa chỉ relay (vd https://relay.example.com/).";
            return false;
        }
        relay = new RelayEndpoint(parsed);
        return true;
    }

    private bool TryReadShares(out List<ShareSettings> shares)
    {
        shares = Shares.Select(x => x.ToSettings()).ToList();
        foreach (ShareSettings share in shares)
        {
            if (share.Name.Length == 0)
            {
                ErrorText = "Thư mục chia sẻ nào cũng phải có tên.";
                return false;
            }
            if (!Directory.Exists(share.Path))
            {
                ErrorText = $"Thư mục của chia sẻ '{share.Name}' không tồn tại: {share.Path}";
                return false;
            }
        }
        string? duplicate = shares.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            ErrorText = $"Tên chia sẻ '{duplicate}' bị trùng.";
            return false;
        }
        return true;
    }

    private static string HostOf(string endpoint)
    {
        int colon = endpoint.LastIndexOf(':');
        return colon > 0 && !endpoint.EndsWith(']') ? endpoint[..colon] : endpoint;
    }

    private void ShowError(Exception e) => ErrorText = e.Message;

    private void Post(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }
}
