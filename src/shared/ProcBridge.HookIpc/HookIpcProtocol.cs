namespace ProcBridge.HookIpc;

/// <summary>
/// Constants of the Hook ↔ Broker IPC (plan section 6). The C++ side mirrors them in
/// <c>native/ProcBridge/HookIpc.h</c>; a test keeps both in sync.
/// </summary>
/// <remarks>
/// Transport: named pipe <c>\\.\pipe\ProcBridge-&lt;session&gt;</c>, message mode, many instances, ACL limited to
/// the current user. One request then one reply per pipe instance at a time; a hook thread that needs
/// concurrency opens another instance.
/// Message: <c>header (16 bytes) | body</c>, all little endian. Strings are <c>u16 charCount | UTF-16 chars</c>
/// with no terminator and no alignment, so the C++ reader copies fields with memcpy.
/// A reply whose header status is not 0 (an NTSTATUS) has no body.
/// </remarks>
public static class HookIpcProtocol
{
    public const ushort Version = 3;

    public const int HeaderSize = 16;

    /// <summary>Largest message in either direction; also the pipe buffer size.</summary>
    public const int MaxMessageSize = 64 * 1024;

    /// <summary>Size of the fixed <c>FileInfo</c> block.</summary>
    public const int FileInfoSize = 64;

    /// <summary>Pipe name without the <c>\\.\pipe\</c> prefix is this plus the session id.</summary>
    public const string PipeNamePrefix = "ProcBridge-";

    /// <summary>Bit 0 of the ListReply flags: more entries after this page.</summary>
    public const ushort ListFlagHasMore = 1;

    /// <summary>Bit 0 of the WatchDirectory flags: watch the whole subtree (<c>WatchTree</c> of NtNotifyChangeDirectoryFile).</summary>
    public const ushort WatchFlagSubtree = 1;

    /// <summary>
    /// Bit 0 of the WaitChanges reply flags: changes were lost (Agent rescan, reconnect, queue full). The hook completes
    /// the pending notify with <c>STATUS_NOTIFY_ENUM_DIR</c> so the process lists the directory again.
    /// </summary>
    public const ushort ChangesFlagOverflow = 1;

    /// <summary>WaitChanges timeout meaning "wait until a change or until the watch is removed".</summary>
    public const uint InfiniteTimeout = 0xFFFFFFFF;
    /// <summary>Bit 0 of the HelloReply flags: send Flush after every write on a handle with a write token.</summary>
    public const ushort HelloFlagWriteThrough = 1;

    /// <summary>Bit 0 of the FetchRange flags: bring the whole file in (memory mapping, inheritance).</summary>
    public const uint FetchFlagWhole = 1;

    /// <summary>Bit 0 of the FetchRange reply flags: the cache copy is now complete, stop asking for this range id.</summary>
    public const uint FetchReplyFlagComplete = 1;

    /// <summary>Bit 0 of the Lock flags: exclusive lock (NtLockFile ExclusiveLock).</summary>
    public const ushort LockFlagExclusive = 1;

    /// <summary>Bit 1 of the Lock flags: fail at once instead of waiting (NtLockFile FailImmediately).</summary>
    public const ushort LockFlagFailImmediately = 2;

    public static string GetPipeName(string sessionId) => PipeNamePrefix + sessionId;

    /// <summary>
    /// New session id: 128 random bits as 32 hex digits. The pipe name is the only thing a process needs to talk
    /// to the Broker, so it must not be guessable (a predictable name lets another process squat on it).
    /// </summary>
    public static string NewSessionId() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
