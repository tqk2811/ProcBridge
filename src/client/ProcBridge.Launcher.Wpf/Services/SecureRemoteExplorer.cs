using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Explorer.Client;
using ProcBridge.Launcher.Wpf.Interfaces;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Launcher.Wpf.Services;

/// <summary><see cref="IRemoteExplorer"/> over an <see cref="ExplorerClient"/> on the encrypted transport; owns both.</summary>
public sealed class SecureRemoteExplorer : IRemoteExplorer
{
    private readonly SecureConnectionFactory _factory;
    private readonly ExplorerClient _client;

    public SecureRemoteExplorer(SecureConnectionFactory factory, ExplorerClient client)
    {
        _factory = factory;
        _client = client;
    }

    public Task<ExplorerPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken) => _client.GetPolicyAsync(cancellationToken);

    public Task<DrivesResponse> ListDrivesAsync(CancellationToken cancellationToken) => _client.ListDrivesAsync(cancellationToken);

    public Task<DirectoryListResponse> ListDirectoryAsync(string path, CancellationToken cancellationToken) => _client.ListDirectoryAsync(path, cancellationToken);

    public Task<ExplorerAckResponse> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => _client.DeleteAsync(path, recursive, cancellationToken);

    public Task<ProcessListResponse> ListProcessesAsync(CancellationToken cancellationToken) => _client.ListProcessesAsync(cancellationToken);

    public Task<ExplorerAckResponse> KillProcessAsync(int pid, bool entireTree, CancellationToken cancellationToken) => _client.KillProcessAsync(pid, entireTree, cancellationToken);

    public Task<RegistryListResponse> ListRegistryAsync(string path, CancellationToken cancellationToken) => _client.ListRegistryAsync(path, cancellationToken);

    public Task<ExplorerAckResponse> DeleteRegistryValueAsync(string path, string name, CancellationToken cancellationToken) => _client.DeleteRegistryValueAsync(path, name, cancellationToken);

    public Task<ServiceListResponse> ListServicesAsync(CancellationToken cancellationToken) => _client.ListServicesAsync(cancellationToken);

    public Task<ExplorerAckResponse> ControlServiceAsync(string name, ServiceControlAction action, CancellationToken cancellationToken) => _client.ControlServiceAsync(name, action, cancellationToken);

    public Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken) => _client.GetSystemInfoAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        _factory.Dispose();
    }
}
