namespace ProcBridge.Protocol.Enums;

/// <summary>
/// NTSTATUS values carried on the wire. Machine B reports the status it would give a local caller so the
/// hooked process sees the same error as when running locally (plan section 7).
/// Values with the customer bit (0x20000000) are ProcBridge specific.
/// </summary>
public enum NtStatus : uint
{
    Success = 0x00000000,
    NoMoreFiles = 0x80000006,
    Unsuccessful = 0xC0000001,
    NotImplemented = 0xC0000002,
    /// <summary>Unknown watch id in a Hook IPC request.</summary>
    InvalidHandle = 0xC0000008,
    InvalidParameter = 0xC000000D,
    EndOfFile = 0xC0000011,
    AccessDenied = 0xC0000022,
    ObjectNameInvalid = 0xC0000033,
    ObjectNameNotFound = 0xC0000034,
    ObjectNameCollision = 0xC0000035,
    ObjectPathNotFound = 0xC000003A,
    SharingViolation = 0xC0000043,
    /// <summary>A byte range lock conflicts with one held by another open (M8).</summary>
    LockNotGranted = 0xC0000055,
    FileLockConflict = 0xC0000054,
    /// <summary>Unlock of a range that is not locked (M8).</summary>
    RangeNotLocked = 0xC000007E,
    RevisionMismatch = 0xC0000059,
    InsufficientResources = 0xC000009A,
    /// <summary>Write attempt on a read-only share (M2).</summary>
    MediaWriteProtected = 0xC00000A2,
    IoTimeout = 0xC00000B5,
    FileIsADirectory = 0xC00000BA,
    NotSupported = 0xC00000BB,
    /// <summary>The share name in Hello is unknown on machine B.</summary>
    BadNetworkName = 0xC00000CC,
    DirectoryNotEmpty = 0xC0000101,
    NotADirectory = 0xC0000103,
    NotSameDevice = 0xC00000D4,
    CannotDelete = 0xC0000121,
    DeletePending = 0xC0000056,
    DiskFull = 0xC000007F,
    Cancelled = 0xC0000120,
    /// <summary>Standard status the Hook gets for a transport or protocol failure that has no closer match.</summary>
    InvalidNetworkResponse = 0xC00000C3,
    UnexpectedNetworkError = 0xC00000C4,
    NetworkUnreachable = 0xC000023C,

    /// <summary>The file on machine B changed between two chunks of one download.</summary>
    FileVersionChanged = 0xE0000001,
    /// <summary>The peer violated the protocol (bad frame, unexpected message).</summary>
    ProtocolError = 0xE0000002,
    /// <summary>A frame exceeded the size limit.</summary>
    FrameTooLarge = 0xE0000003,
}
