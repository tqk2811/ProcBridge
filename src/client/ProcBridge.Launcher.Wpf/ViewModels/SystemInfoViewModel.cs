using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;

namespace ProcBridge.Launcher.Wpf.ViewModels;

/// <summary>System tab: machine name, Windows, CPU/RAM, uptime, account of the Agent, networks (read only).</summary>
public sealed class SystemInfoViewModel : ExplorerPageViewModel
{
    private SystemInfoResponse? _info;

    public SystemInfoViewModel(IRemoteExplorer explorer, ExplorerPermissions granted, IUserDialogs dialogs)
        : base(explorer, granted, dialogs, ExplorerPermissions.SystemInfo)
    {
    }

    public SystemInfoResponse? Info
    {
        get => _info;
        private set
        {
            if (SetProperty(ref _info, value))
                OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Lines label: value, ready to show.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Summary => _info is null ? [] :
    [
        new("Tên máy", _info.MachineName ?? string.Empty),
        new("Windows", $"{_info.OsDescription} ({_info.OsVersion}, {(_info.Is64BitOs ? "64-bit" : "32-bit")})"),
        new("CPU", $"{_info.ProcessorCount} luồng"),
        new("RAM", $"{_info.TotalMemoryBytes / (1024.0 * 1024 * 1024):0.0} GB"),
        new("Agent chạy bằng", _info.AgentUser ?? string.Empty),
        new("Thời gian chạy", TimeSpan.FromMilliseconds(_info.UptimeMilliseconds).ToString(@"d\.hh\:mm\:ss")),
        .. _info.Networks.Select(x => new KeyValuePair<string, string>($"Mạng {x.Name}", string.Join(", ", x.Addresses))),
    ];

    protected override async Task<ExplorerResponse> LoadCoreAsync()
    {
        SystemInfoResponse response = await Explorer.GetSystemInfoAsync(CancellationToken.None);
        Info = response.Status == ExplorerStatus.Ok ? response : null;
        return response;
    }
}
