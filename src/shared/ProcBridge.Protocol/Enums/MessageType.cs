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
    /// <summary>Subscribes the connection to change notifications of the share (plan section 10, M7).</summary>
    FsWatch = 0x0104,
    /// <summary>Notification frame: <see cref="Models.FsChangeNotification"/>.</summary>
    FsChanged = 0x0105,
    /// <summary>Metadata of a whole subtree in pages (plan section 11: fetch the tree once).</summary>
    FsTree = 0x0106,
}
