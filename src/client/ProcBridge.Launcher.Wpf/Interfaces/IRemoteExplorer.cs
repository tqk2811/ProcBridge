using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Launcher.Wpf.Interfaces;

/// <summary>The Remote Explorer calls the UI uses (an <c>ExplorerClient</c> in production, a fake in tests).</summary>
public interface IRemoteExplorer : IAsyncDisposable
{
    Task<ExplorerPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken);

    Task<DrivesResponse> ListDrivesAsync(CancellationToken cancellationToken);

    Task<DirectoryListResponse> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    Task<ExplorerAckResponse> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken);

    Task<ProcessListResponse> ListProcessesAsync(CancellationToken cancellationToken);

    Task<ExplorerAckResponse> KillProcessAsync(int pid, bool entireTree, CancellationToken cancellationToken);

    Task<RegistryListResponse> ListRegistryAsync(string path, CancellationToken cancellationToken);

    Task<ExplorerAckResponse> DeleteRegistryValueAsync(string path, string name, CancellationToken cancellationToken);

    Task<ServiceListResponse> ListServicesAsync(CancellationToken cancellationToken);

    Task<ExplorerAckResponse> ControlServiceAsync(string name, ServiceControlAction action, CancellationToken cancellationToken);

    Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken);
}
