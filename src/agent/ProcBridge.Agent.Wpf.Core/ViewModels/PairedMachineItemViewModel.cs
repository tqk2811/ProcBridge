using ProcBridge.Agent.Wpf.Core.Mvvm;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent.Wpf.Core.ViewModels;

/// <summary>One paired machine A in the list.</summary>
public sealed class PairedMachineItemViewModel(PairedMachine machine) : ObservableObject
{
    private DateTime? _lastSeenUtc;
    private int _connectionCount;

    public string Fingerprint { get; } = machine.Fingerprint;

    public string Name { get; } = machine.Name;

    public DateTime PairedAtLocal { get; } = machine.PairedAtUtc.ToLocalTime();

    public DateTime? LastSeenUtc
    {
        get => _lastSeenUtc;
        set
        {
            if (SetProperty(ref _lastSeenUtc, value))
                OnPropertyChanged(nameof(LastSeenText));
        }
    }

    public string LastSeenText => ConnectionCount > 0 ? "Đang kết nối" : LastSeenUtc is { } seen ? seen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Chưa thấy";

    /// <summary>Open connections of this machine right now.</summary>
    public int ConnectionCount
    {
        get => _connectionCount;
        set
        {
            if (SetProperty(ref _connectionCount, value))
                OnPropertyChanged(nameof(LastSeenText));
        }
    }
}
