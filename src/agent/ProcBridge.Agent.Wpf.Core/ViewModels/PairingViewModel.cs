using System.Collections.ObjectModel;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Mvvm;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>
/// Pairing dialog of machine B (plan section 9, backlog "UI ghép cặp phía B phải bắt so vân tay A"). The 6-digit code
/// only travels B to A, so it cannot prove the request comes from the real machine A: the user must compare A's
/// fingerprint too. One group of the fingerprint is hidden and the user picks it, as read on A's screen, among
/// <see cref="ChoiceCount"/> options; Accept stays disabled until the right one is picked. Each wrong pick hides another
/// group with new options; after <see cref="MaxWrongAttempts"/> wrong picks the request is rejected.
/// Closing the dialog without a decision rejects it.
/// </summary>
public sealed class PairingViewModel : ObservableObject
{
    public const int ChoiceCount = 4;
    public const int MaxWrongAttempts = 3;
    public const string HiddenGroup = "????";

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private readonly IPairingPrompt _prompt;
    private readonly Random _random;
    private readonly string[] _groups;
    private int _hiddenIndex;
    private int _wrongAttempts;
    private bool _isFingerprintConfirmed;
    private bool _isDecided;
    private string? _errorText;
    private string _maskedFingerprint = string.Empty;

    /// <param name="random">Picks the hidden group and the decoys; tests pass a seeded one.</param>
    public PairingViewModel(IPairingPrompt prompt, Random? random = null)
    {
        _prompt = prompt;
        _random = random ?? Random.Shared;
        _groups = prompt.PeerFingerprint.Split('-');
        AcceptCommand = new RelayCommand(() => Accept(), () => IsFingerprintConfirmed && !_isDecided);
        RejectCommand = new RelayCommand(() => Reject(), () => !_isDecided);
        ChooseCommand = new RelayCommand(x => Choose((string)x!), x => x is string && !_isDecided && !IsFingerprintConfirmed);
        NewChallenge();
        // Timed out, or A gave up: nothing left to decide.
        _ = prompt.Completion.ContinueWith(_ => CloseRequested?.Invoke(), TaskScheduler.Default);
    }

    /// <summary>Asks the window to close (may be raised on a background thread when the request ends by itself).</summary>
    public event Action? CloseRequested;

    public string Code => _prompt.Code;

    public string PeerName => _prompt.PeerName;

    public string RemoteAddress => _prompt.RemoteAddress;

    /// <summary>Full fingerprint of A, shown once confirmed.</summary>
    public string PeerFingerprint => _prompt.PeerFingerprint;

    /// <summary>Fingerprint with the group to pick replaced by <see cref="HiddenGroup"/> (or the full one once confirmed).</summary>
    public string MaskedFingerprint
    {
        get => _maskedFingerprint;
        private set => SetProperty(ref _maskedFingerprint, value);
    }

    /// <summary>1-based position of the hidden group.</summary>
    public int HiddenGroupNumber => _hiddenIndex + 1;

    public ObservableCollection<string> Choices { get; } = [];

    public int WrongAttempts => _wrongAttempts;

    public bool IsFingerprintConfirmed
    {
        get => _isFingerprintConfirmed;
        private set
        {
            if (SetProperty(ref _isFingerprintConfirmed, value))
                RaiseCommands();
        }
    }

    public bool IsDecided => _isDecided;

    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public RelayCommand AcceptCommand { get; }

    public RelayCommand RejectCommand { get; }

    /// <summary>Parameter: the chosen group text.</summary>
    public RelayCommand ChooseCommand { get; }

    /// <summary>The user picks <paramref name="group"/> as the hidden group; true when it matches A's fingerprint.</summary>
    public bool Choose(string group)
    {
        if (_isDecided || IsFingerprintConfirmed)
            return false;
        if (string.Equals(group, _groups[_hiddenIndex], StringComparison.Ordinal))
        {
            ErrorText = null;
            MaskedFingerprint = PeerFingerprint;
            IsFingerprintConfirmed = true;
            return true;
        }
        _wrongAttempts++;
        OnPropertyChanged(nameof(WrongAttempts));
        if (_wrongAttempts >= MaxWrongAttempts)
        {
            ErrorText = "Chọn sai quá nhiều lần: vân tay không khớp, yêu cầu đã bị từ chối.";
            Reject();
            return false;
        }
        ErrorText = "Không khớp. Nếu màn hình máy A đúng là nhóm khác, có thể đang có máy giả danh: hãy Từ chối. Thử lại với nhóm mới.";
        NewChallenge();
        return false;
    }

    /// <summary>Approves machine A; does nothing (returns false) until the fingerprint was confirmed.</summary>
    public bool Accept()
    {
        if (!IsFingerprintConfirmed || _isDecided)
            return false;
        Decide();
        _prompt.Accept();
        CloseRequested?.Invoke();
        return true;
    }

    public void Reject()
    {
        if (_isDecided)
            return;
        Decide();
        _prompt.Reject();
        CloseRequested?.Invoke();
    }

    private void Decide()
    {
        _isDecided = true;
        OnPropertyChanged(nameof(IsDecided));
        RaiseCommands();
    }

    private void NewChallenge()
    {
        int previous = Choices.Count == 0 ? -1 : _hiddenIndex;
        do
            _hiddenIndex = _random.Next(_groups.Length);
        while (_groups.Length > 1 && _hiddenIndex == previous);

        string correct = _groups[_hiddenIndex];
        var options = new List<string> { correct };
        while (options.Count < ChoiceCount)
        {
            string decoy = RandomGroup(correct.Length);
            if (!options.Contains(decoy) && !_groups.Contains(decoy))
                options.Add(decoy);
        }
        Choices.Clear();
        foreach (string option in options.OrderBy(_ => _random.Next()))
            Choices.Add(option);
        MaskedFingerprint = string.Join('-', _groups.Select((x, i) => i == _hiddenIndex ? HiddenGroup : x));
        OnPropertyChanged(nameof(HiddenGroupNumber));
    }

    private string RandomGroup(int length)
    {
        Span<char> chars = stackalloc char[length];
        for (int i = 0; i < length; i++)
            chars[i] = Base32Alphabet[_random.Next(Base32Alphabet.Length)];
        return new string(chars);
    }

    private void RaiseCommands()
    {
        AcceptCommand.RaiseCanExecuteChanged();
        RejectCommand.RaiseCanExecuteChanged();
        ChooseCommand.RaiseCanExecuteChanged();
    }
}
