using System.Collections.Concurrent;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Launcher.Wpf.Interfaces;

namespace ProcBridge.Launcher.Wpf.Tests.Fakes;

/// <summary>Scripted machine B: folders by path, a status per refused path, and a log of every call.</summary>
internal sealed class FakeRemoteExplorer : IRemoteExplorer
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public ExplorerPolicyResponse Policy { get; set; } = new();

    public DrivesResponse Drives { get; set; } = new();

    /// <summary>Folder path → entries.</summary>
    public Dictionary<string, FileEntry[]> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder path → status returned instead of the listing.</summary>
    public Dictionary<string, ExplorerStatus> RefusedFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ProcessListResponse Processes { get; set; } = new();

    public ExplorerStatus WriteStatus { get; set; } = ExplorerStatus.Ok;

    public bool Disposed { get; private set; }

    public static FileEntry Dir(string parent, string name) => new() { Name = name, FullPath = parent.TrimEnd('\\') + "\\" + name, IsDirectory = true };

    public static FileEntry File(string parent, string name) => new() { Name = name, FullPath = parent.TrimEnd('\\') + "\\" + name, Size = 3 };

    public Task<ExplorerPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken) => Log("policy", Policy);

    public ShareEntry[] Shares { get; set; } = [];

    /// <summary>Thrown by <see cref="ListSharesAsync"/> (an old Agent, a lost connection).</summary>
    public Exception? SharesFailure { get; set; }

    public Task<ShareListResponse> ListSharesAsync(CancellationToken cancellationToken)
    {
        if (SharesFailure is not null)
        {
            Calls.Enqueue("shares");
            return Task.FromException<ShareListResponse>(SharesFailure);
        }
        return Log("shares", new ShareListResponse { Shares = Shares });
    }

    public Task<DrivesResponse> ListDrivesAsync(CancellationToken cancellationToken) => Log("drives", Drives);

    public Task<DirectoryListResponse> ListDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (RefusedFolders.TryGetValue(path, out ExplorerStatus status))
            return Log($"list {path}", new DirectoryListResponse { Status = status, Error = "scripted" });
        return Log($"list {path}", new DirectoryListResponse { Path = path, Entries = Folders.TryGetValue(path, out FileEntry[]? entries) ? entries : [] });
    }

    public Task<ExplorerAckResponse> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => Log($"delete {path}", Ack());

    public Task<ProcessListResponse> ListProcessesAsync(CancellationToken cancellationToken) => Log("processes", Processes);

    public Task<ExplorerAckResponse> KillProcessAsync(int pid, bool entireTree, CancellationToken cancellationToken) => Log($"kill {pid} tree={entireTree}", Ack());

    public Task<RegistryListResponse> ListRegistryAsync(string path, CancellationToken cancellationToken) => Log($"reg {path}", new RegistryListResponse { Path = path });

    public Task<ExplorerAckResponse> DeleteRegistryValueAsync(string path, string name, CancellationToken cancellationToken) => Log($"regdel {path} {name}", Ack());

    public Task<ServiceListResponse> ListServicesAsync(CancellationToken cancellationToken) => Log("services", new ServiceListResponse());

    public Task<ExplorerAckResponse> ControlServiceAsync(string name, ServiceControlAction action, CancellationToken cancellationToken) => Log($"service {name} {action}", Ack());

    public Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken) => Log("sysinfo", new SystemInfoResponse { MachineName = "B" });

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private ExplorerAckResponse Ack() => new() { Status = WriteStatus };

    private Task<T> Log<T>(string call, T response)
    {
        Calls.Enqueue(call);
        return Task.FromResult(response);
    }
}
