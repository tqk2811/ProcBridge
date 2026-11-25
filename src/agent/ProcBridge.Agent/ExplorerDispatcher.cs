using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Routes the Remote Explorer messages of one connection (plan section 14) to the <see cref="ExplorerSession"/> built
/// from the policy machine B keeps for the authenticated machine A. A refused operation is a normal response whose
/// status says why; only malformed requests become error frames. File downloads return the bytes as the raw frame
/// payload, not inside the MessagePack header.
/// </summary>
internal sealed class ExplorerDispatcher
{
    private readonly ExplorerSession _session;
    private readonly IReadOnlyList<ShareEntry> _shares;

    /// <param name="shares">Shares of the Agent, answered to <see cref="MessageType.ExplorerSysShares"/>.</param>
    public ExplorerDispatcher(ExplorerSession session, IReadOnlyList<ShareEntry> shares)
    {
        _session = session;
        _shares = shares;
    }

    public static bool IsExplorerMessage(MessageType type) => (ushort)type is >= 0x1100 and < 0x1500;

    /// <summary>Handles one request; returns the response header and payload.</summary>
    /// <exception cref="ProtocolStatusException">Malformed request or unknown Explorer message.</exception>
    public (byte[] Header, ReadOnlyMemory<byte> Payload) Handle(Frame request)
    {
        switch (request.MessageType)
        {
            case MessageType.ExplorerFsRead:
            {
                FileReadResponse response = _session.Files.ReadFile(Read<FileReadRequest>(request));
                byte[] data = response.Data;
                response.Data = [];
                return (ProtocolSerializer.Serialize(response), data);
            }
            case MessageType.ExplorerFsList:
                return Reply(_session.Files.ListDirectory(Read<DirectoryListRequest>(request)));
            case MessageType.ExplorerFsStat:
                return Reply(_session.Files.Stat(Read<FileStatRequest>(request)));
            case MessageType.ExplorerFsDelete:
                return Reply(_session.Files.Delete(Read<FileDeleteRequest>(request)));
            case MessageType.ExplorerFsRename:
                return Reply(_session.Files.Rename(Read<FileRenameRequest>(request)));
            case MessageType.ExplorerFsCreateDirectory:
                return Reply(_session.Files.CreateDirectory(Read<DirectoryCreateRequest>(request)));
            case MessageType.ExplorerProcList:
                return Reply(_session.Processes.List(Read<ProcessListRequest>(request)));
            case MessageType.ExplorerProcKill:
                return Reply(_session.Processes.Kill(Read<ProcessKillRequest>(request)));
            case MessageType.ExplorerRegList:
                return Reply(_session.Registry.List(Read<RegistryListRequest>(request)));
            case MessageType.ExplorerRegSetValue:
                return Reply(_session.Registry.SetValue(Read<RegistrySetValueRequest>(request)));
            case MessageType.ExplorerRegDeleteValue:
                return Reply(_session.Registry.DeleteValue(Read<RegistryDeleteValueRequest>(request)));
            case MessageType.ExplorerRegCreateKey:
                return Reply(_session.Registry.CreateKey(Read<RegistryCreateKeyRequest>(request)));
            case MessageType.ExplorerRegDeleteKey:
                return Reply(_session.Registry.DeleteKey(Read<RegistryDeleteKeyRequest>(request)));
            case MessageType.ExplorerSysDrives:
                return Reply(_session.Files.ListDrives(Read<DrivesRequest>(request)));
            case MessageType.ExplorerSysServices:
                return Reply(_session.Services.List(Read<ServiceListRequest>(request)));
            case MessageType.ExplorerSysServiceControl:
                return Reply(_session.Services.Control(Read<ServiceControlRequest>(request)));
            case MessageType.ExplorerSysInfo:
                return Reply(_session.SystemInfo.Get(Read<SystemInfoRequest>(request)));
            case MessageType.ExplorerSysPolicy:
                Read<ExplorerPolicyRequest>(request);
                return Reply(new ExplorerPolicyResponse
                {
                    Permissions = _session.Policy.Permissions,
                    FileRoots = _session.Policy.FileRoots,
                    RegistryRoots = _session.Policy.RegistryRoots,
                });
            case MessageType.ExplorerSysShares:
                Read<ShareListRequest>(request);
                return Reply(_session.ListShares(_shares));
            default:
                throw new ProtocolStatusException(NtStatus.NotImplemented, $"Message {request.MessageType} is not supported.");
        }
    }

    private static T Read<T>(Frame request) => ProtocolSerializer.Deserialize<T>(request.Header);

    private static (byte[], ReadOnlyMemory<byte>) Reply<T>(T response) => (ProtocolSerializer.Serialize(response), default);
}
