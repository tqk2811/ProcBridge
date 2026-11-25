using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Launcher.Wpf.Mvvm;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>
/// Pairing a new machine B (plan section 9): the user types the Agent address, compares fingerprints (this machine's
/// is shown all along, the Agent's once the handshake ran) and types the 6 digit code machine B shows.
/// </summary>
public sealed class PairingViewModel : ObservableObject
{
    private readonly IPairedMachineRegistry _registry;
    private TaskCompletionSource<string>? _code;
    private CancellationTokenSource? _cancel;
    private string _address = string.Empty;
    private string _code6 = string.Empty;
    private string? _agentFingerprint;
    private string? _statusText;
    private bool _isBusy;
    private bool _isWaitingForCode;

    public PairingViewModel(IPairedMachineRegistry registry)
    {
        _registry = registry;
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy);
        ConfirmCodeCommand = new RelayCommand(ConfirmCode, () => IsWaitingForCode);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    /// <summary>Raised on the UI thread after a successful pairing.</summary>
    public event Action<PairedMachine>? Paired;

    public string OwnFingerprint => _registry.OwnFingerprint;

    public AsyncRelayCommand StartCommand { get; }

    public RelayCommand ConfirmCodeCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary><c>host[:port]</c> of the Agent.</summary>
    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value);
    }

    public string Code
    {
        get => _code6;
        set => SetProperty(ref _code6, value);
    }

    /// <summary>Fingerprint the Agent presented; the user compares it with what machine B shows.</summary>
    public string? AgentFingerprint
    {
        get => _agentFingerprint;
        private set => SetProperty(ref _agentFingerprint, value);
    }

    public string? StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                StartCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsWaitingForCode
    {
        get => _isWaitingForCode;
        private set
        {
            if (SetProperty(ref _isWaitingForCode, value))
                ConfirmCodeCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task StartAsync()
    {
        if (string.IsNullOrWhiteSpace(Address))
        {
            StatusText = "Nhập địa chỉ máy B (host hoặc host:port).";
            return;
        }
        (string host, int port) = LauncherCommandLine.ParseAgent(Address.Trim());
        AgentFingerprint = null;
        Code = string.Empty;
        IsBusy = true;
        StatusText = $"Đang kết nối {host}:{port}…";
        _cancel = new CancellationTokenSource();
        try
        {
            PairedMachine machine = await _registry.PairAsync(host, port, AskCodeAsync, _cancel.Token);
            StatusText = $"Đã ghép cặp với {machine.Name} ({machine.Fingerprint}). Máy B cần bấm chấp nhận nếu chưa.";
            Paired?.Invoke(machine);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Đã huỷ ghép cặp.";
        }
        catch (Exception e)
        {
            StatusText = $"Ghép cặp thất bại: {e.Message}";
        }
        finally
        {
            IsWaitingForCode = false;
            IsBusy = false;
            _code = null;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    private Task<string> AskCodeAsync(string agentFingerprint, CancellationToken cancellationToken)
    {
        var code = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => code.TrySetCanceled(cancellationToken));
        _code = code;
        AgentFingerprint = agentFingerprint;
        StatusText = "So vân tay của máy B với vân tay hiện trên máy B, rồi nhập mã 6 số máy B hiển thị.";
        IsWaitingForCode = true;
        return code.Task;
    }

    private void ConfirmCode()
    {
        string code = Code.Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            StatusText = "Mã ghép cặp gồm đúng 6 chữ số.";
            return;
        }
        IsWaitingForCode = false;
        StatusText = "Đang xác nhận mã…";
        _code?.TrySetResult(code);
    }

    private void Cancel() => _cancel?.Cancel();
}
