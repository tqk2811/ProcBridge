namespace ProcBridge.Protocol.Enums;

/// <summary>
/// Message identifiers. The high byte is the domain (plan section 7) so registry, process and socket
/// messages can be added later without touching the frame layout.
/// </summary>
public enum MessageType : ushort
{
    /// <summary>Connection handshake: protocol version and share name.</summary>
    Hello = 0x0001,

    FsStat = 0x0101,
    FsList = 0x0102,
    /// <summary>Response header is followed by the raw file bytes as the frame payload.</summary>
    FsRead = 0x0103,
    /// <summary>Creates a file or directory (M3).</summary>
    FsCreate = 0x0104,
    /// <summary>One block of an upload; the request payload is the bytes.</summary>
    FsWrite = 0x0105,
    /// <summary>Ends an upload with the version check (plan section 10).</summary>
    FsCommit = 0x0106,
    FsSetInfo = 0x0107,
    FsRename = 0x0108,
    FsDelete = 0x0109,
}
