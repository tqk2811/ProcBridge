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
}
