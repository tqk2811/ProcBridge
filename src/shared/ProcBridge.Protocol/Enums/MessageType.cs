namespace ProcBridge.Protocol.Enums;

/// <summary>
/// Message identifiers. The high byte is the domain (plan section 7) so registry, process and socket
/// messages can be added later without touching the frame layout.
/// </summary>
public enum MessageType : ushort
{
    /// <summary>Connection handshake: protocol version and share name.</summary>
    Hello = 0x0001,
    /// <summary>Keepalive probe (empty header both ways); answered at once, outside the request limit of the connection.</summary>
    Ping = 0x0002,

    FsStat = 0x0101,
    FsList = 0x0102,
    /// <summary>Response header is followed by the raw file bytes as the frame payload.</summary>
    FsRead = 0x0103,
    /// <summary>Subscribes the connection to change notifications of the share (plan section 10, M7).</summary>
    FsWatch = 0x0104,
    /// <summary>Notification frame: <see cref="Models.FsChangeNotification"/>.</summary>
    FsChanged = 0x0105,
    /// <summary>Metadata of a whole subtree in pages (plan section 11: fetch the tree once).</summary>
    FsTree = 0x0106,

    // Remote Explorer (plan section 14), answered by the Agent under the ExplorerPolicy it keeps for the paired machine A.
    // Requests and responses are the ProcBridge.Agent.Explorer models; a denied operation is an Ok frame whose response
    // carries the status. 0x11xx browses files and drives outside the shares, 0x12xx processes, 0x13xx registry,
    // 0x14xx services and system information.

    ExplorerFsList = 0x1101,
    ExplorerFsStat = 0x1102,
    /// <summary>Response header is a FileReadResponse without data; the bytes follow as the raw frame payload.</summary>
    ExplorerFsRead = 0x1103,
    ExplorerFsDelete = 0x1104,
    ExplorerFsRename = 0x1105,
    ExplorerFsCreateDirectory = 0x1106,
    ExplorerProcList = 0x1201,
    ExplorerProcKill = 0x1202,
    ExplorerRegList = 0x1301,
    ExplorerRegSetValue = 0x1302,
    ExplorerRegDeleteValue = 0x1303,
    ExplorerRegCreateKey = 0x1304,
    ExplorerRegDeleteKey = 0x1305,
    ExplorerSysDrives = 0x1401,
    ExplorerSysServices = 0x1402,
    ExplorerSysServiceControl = 0x1403,
    ExplorerSysInfo = 0x1404,
    /// <summary>The profile machine B keeps for the caller (ExplorerPolicyResponse); never refused.</summary>
    ExplorerSysPolicy = 0x1405,
    /// <summary>Creates a file or directory (M3).</summary>
    FsCreate = 0x0107,
    /// <summary>One block of an upload; the request payload is the bytes.</summary>
    FsWrite = 0x0108,
    /// <summary>Ends an upload with the version check (plan section 10).</summary>
    FsCommit = 0x0109,
    FsSetInfo = 0x010A,
    FsRename = 0x010B,
    FsDelete = 0x010C,
    /// <summary>Opens a handle machine B keeps for an open on machine A (M8, locks and share mode).</summary>
    FsOpenHandle = 0x010D,
    FsCloseHandle = 0x010E,
    /// <summary>Byte range lock on a handle of FsOpenHandle (M8).</summary>
    FsLock = 0x010F,
    FsUnlock = 0x0110,
}
