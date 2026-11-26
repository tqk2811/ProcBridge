namespace ProcBridge.HookIpc.Enums;

/// <summary>Request type; the reply carries the same value.</summary>
public enum HookIpcMessageType : ushort
{
    /// <summary>Hook announces itself and receives the virtual roots of the session.</summary>
    Hello = 1,
    /// <summary>Metadata of a virtual path without opening it (NtQueryAttributesFile, NtQueryFullAttributesFile).</summary>
    Stat = 2,
    /// <summary>Makes sure a virtual path is in the cache and returns the cache path to open (NtCreateFile, NtOpenFile).</summary>
    Resolve = 3,
    /// <summary>One page of a virtual directory listing (NtQueryDirectoryFile[Ex]).</summary>
    List = 4,
    /// <summary>Starts watching a virtual directory for changes (NtNotifyChangeDirectoryFile[Ex]); returns a watch id.</summary>
    WatchDirectory = 5,
    /// <summary>Long poll: waits until a watch has changes (or the timeout) and returns them.</summary>
    WaitChanges = 6,
    /// <summary>Stops a watch (the directory handle was closed).</summary>
    Unwatch = 7,
    /// <summary>A handle holding a write token was closed: write the cache copy back to machine B when it changed (M3).</summary>
    Close = 8,
    /// <summary>NtFlushBuffersFile (or a write in write-through mode) on a handle with a write token.</summary>
    Flush = 9,
    /// <summary>Attributes or times set through a virtual handle (FileBasicInformation).</summary>
    SetInfo = 10,
    /// <summary>Rename or move inside the virtual folders.</summary>
    Rename = 11,
    /// <summary>Delete a virtual file or empty directory (disposition, delete-on-close, NtDeleteFile).</summary>
    Delete = 12,
    // 13 was Retain (protocol 5): replaced by Share, not to be used again.
    /// <summary>A read (or a mapping) on a handle whose cache copy is only partly downloaded: bring the range in first (M8).</summary>
    FetchRange = 14,
    /// <summary>NtLockFile on a handle with a write token: the lock is taken on machine B too (M8).</summary>
    Lock = 15,
    /// <summary>NtUnlockFile on a handle with a write token (M8).</summary>
    Unlock = 16,
    /// <summary>Long poll for every watch of the process at once: waits until one of them has changes and returns them by watch.</summary>
    WaitAnyChanges = 17,
    /// <summary>A handle holding a write token went to another process (duplicated, inherited): that process holds the token too.</summary>
    Share = 18,
    /// <summary>A handle the hook did not open turned out to be a virtual one: machine B's metadata and the write token of this process.</summary>
    Adopt = 19,
}
