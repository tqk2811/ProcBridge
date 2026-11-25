using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Explorer.Client;

/// <summary>
/// Remote Explorer of one Agent, seen from machine A (plan section 14): drives, files, processes, registry, services
/// and system information of machine B, within the profile machine B granted this machine. Uses its own
/// <see cref="ConnectionPool"/> (Hello without a share) over the given factory, normally the encrypted one; owns the
/// pool, not the factory. A refused operation comes back as a response whose <see cref="ExplorerResponse.Status"/>
/// says why; transport failures and an Agent that refuses the Explorer throw <see cref="ProtocolStatusException"/>.
/// </summary>
public sealed class ExplorerClient : IAsyncDisposable
{
    private readonly ConnectionPool _pool;

    public ExplorerClient(IConnectionFactory factory, TransportOptions options, string clientName = "ProcBridge.Explorer")
    {
        var hello = new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = clientName, ShareName = string.Empty };
        _pool = new ConnectionPool(factory, hello, options);
    }

    public Task<DrivesResponse> ListDrivesAsync(CancellationToken cancellationToken)
        => CallAsync<DrivesRequest, DrivesResponse>(MessageType.ExplorerSysDrives, new DrivesRequest(), true, cancellationToken);

    public Task<DirectoryListResponse> ListDirectoryAsync(string path, CancellationToken cancellationToken)
        => CallAsync<DirectoryListRequest, DirectoryListResponse>(MessageType.ExplorerFsList, new DirectoryListRequest { Path = path }, true, cancellationToken);

    public Task<FileStatResponse> StatAsync(string path, CancellationToken cancellationToken)
        => CallAsync<FileStatRequest, FileStatResponse>(MessageType.ExplorerFsStat, new FileStatRequest { Path = path }, true, cancellationToken);

    /// <summary>Reads one chunk; the bytes travel as the raw frame payload and land in <see cref="FileReadResponse.Data"/>.</summary>
    public async Task<FileReadResponse> ReadFileAsync(string path, long offset, int count, CancellationToken cancellationToken)
    {
        byte[] header = ProtocolSerializer.Serialize(new FileReadRequest { Path = path, Offset = offset, Count = count });
        Frame frame = await _pool.SendAsync(MessageType.ExplorerFsRead, header, default, idempotent: true, cancellationToken).ConfigureAwait(false);
        FileReadResponse response = ProtocolSerializer.Deserialize<FileReadResponse>(frame.Header);
        response.Data = frame.Payload.ToArray();
        return response;
    }

    /// <summary>
    /// Downloads a whole file into <paramref name="destination"/> chunk by chunk. Returns the status of the first refused
    /// chunk, or <see cref="ExplorerStatus.Ok"/>; the destination may hold a partial file on failure.
    /// </summary>
    public async Task<ExplorerStatus> DownloadAsync(string path, Stream destination, CancellationToken cancellationToken, int chunkSize = 256 * 1024)
    {
        long offset = 0;
        while (true)
        {
            FileReadResponse chunk = await ReadFileAsync(path, offset, chunkSize, cancellationToken).ConfigureAwait(false);
            if (chunk.Status != ExplorerStatus.Ok)
                return chunk.Status;
            await destination.WriteAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
            offset += chunk.Data.Length;
            if (chunk.Data.Length == 0 || offset >= chunk.FileLength)
                return ExplorerStatus.Ok;
        }
    }

    public Task<ExplorerAckResponse> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
        => CallAsync<FileDeleteRequest, ExplorerAckResponse>(MessageType.ExplorerFsDelete, new FileDeleteRequest { Path = path, Recursive = recursive }, false, cancellationToken);

    public Task<ExplorerAckResponse> RenameAsync(string path, string newPath, CancellationToken cancellationToken)
        => CallAsync<FileRenameRequest, ExplorerAckResponse>(MessageType.ExplorerFsRename, new FileRenameRequest { Path = path, NewPath = newPath }, false, cancellationToken);

    public Task<ExplorerAckResponse> CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        => CallAsync<DirectoryCreateRequest, ExplorerAckResponse>(MessageType.ExplorerFsCreateDirectory, new DirectoryCreateRequest { Path = path }, false, cancellationToken);

    public Task<ProcessListResponse> ListProcessesAsync(CancellationToken cancellationToken)
        => CallAsync<ProcessListRequest, ProcessListResponse>(MessageType.ExplorerProcList, new ProcessListRequest(), true, cancellationToken);

    public Task<ExplorerAckResponse> KillProcessAsync(int pid, bool entireTree, CancellationToken cancellationToken)
        => CallAsync<ProcessKillRequest, ExplorerAckResponse>(MessageType.ExplorerProcKill, new ProcessKillRequest { Pid = pid, EntireTree = entireTree }, false, cancellationToken);

    public Task<RegistryListResponse> ListRegistryAsync(string path, CancellationToken cancellationToken)
        => CallAsync<RegistryListRequest, RegistryListResponse>(MessageType.ExplorerRegList, new RegistryListRequest { Path = path }, true, cancellationToken);

    public Task<ExplorerAckResponse> SetRegistryValueAsync(string path, RegistryValue value, CancellationToken cancellationToken)
        => CallAsync<RegistrySetValueRequest, ExplorerAckResponse>(MessageType.ExplorerRegSetValue, new RegistrySetValueRequest { Path = path, Value = value }, false, cancellationToken);

    public Task<ExplorerAckResponse> DeleteRegistryValueAsync(string path, string name, CancellationToken cancellationToken)
        => CallAsync<RegistryDeleteValueRequest, ExplorerAckResponse>(MessageType.ExplorerRegDeleteValue, new RegistryDeleteValueRequest { Path = path, Name = name }, false, cancellationToken);

    public Task<ExplorerAckResponse> CreateRegistryKeyAsync(string path, CancellationToken cancellationToken)
        => CallAsync<RegistryCreateKeyRequest, ExplorerAckResponse>(MessageType.ExplorerRegCreateKey, new RegistryCreateKeyRequest { Path = path }, false, cancellationToken);

    public Task<ExplorerAckResponse> DeleteRegistryKeyAsync(string path, bool recursive, CancellationToken cancellationToken)
        => CallAsync<RegistryDeleteKeyRequest, ExplorerAckResponse>(MessageType.ExplorerRegDeleteKey, new RegistryDeleteKeyRequest { Path = path, Recursive = recursive }, false, cancellationToken);

    public Task<ServiceListResponse> ListServicesAsync(CancellationToken cancellationToken)
        => CallAsync<ServiceListRequest, ServiceListResponse>(MessageType.ExplorerSysServices, new ServiceListRequest(), true, cancellationToken);

    public Task<ExplorerAckResponse> ControlServiceAsync(string name, ServiceControlAction action, CancellationToken cancellationToken)
        => CallAsync<ServiceControlRequest, ExplorerAckResponse>(MessageType.ExplorerSysServiceControl, new ServiceControlRequest { Name = name, Action = action }, false, cancellationToken);

    public Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken)
        => CallAsync<SystemInfoRequest, SystemInfoResponse>(MessageType.ExplorerSysInfo, new SystemInfoRequest(), true, cancellationToken);

    /// <summary>The profile machine B granted this machine; lets the UI hide what would be refused (the Agent still checks).</summary>
    public Task<ExplorerPolicyResponse> GetPolicyAsync(CancellationToken cancellationToken)
        => CallAsync<ExplorerPolicyRequest, ExplorerPolicyResponse>(MessageType.ExplorerSysPolicy, new ExplorerPolicyRequest(), true, cancellationToken);

    /// <summary>
    /// The shares of the Agent (name and folder on machine B), for picking the share of a run. An Agent older than this
    /// message throws <see cref="ProtocolStatusException"/> with <see cref="NtStatus.NotImplemented"/>.
    /// </summary>
    public Task<ShareListResponse> ListSharesAsync(CancellationToken cancellationToken)
        => CallAsync<ShareListRequest, ShareListResponse>(MessageType.ExplorerSysShares, new ShareListRequest(), true, cancellationToken);

    /// <param name="idempotent">Reads may be resent after a broken connection; write operations never are.</param>
    private async Task<TResponse> CallAsync<TRequest, TResponse>(MessageType type, TRequest request, bool idempotent, CancellationToken cancellationToken)
    {
        Frame frame = await _pool.SendAsync(type, ProtocolSerializer.Serialize(request), default, idempotent, cancellationToken).ConfigureAwait(false);
        return ProtocolSerializer.Deserialize<TResponse>(frame.Header);
    }

    public ValueTask DisposeAsync() => _pool.DisposeAsync();
}
