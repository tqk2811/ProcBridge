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
    /// <summary>A handle holding a write token was closed: write the cache copy back to machine B when it changed (M3).</summary>
    Close = 5,
    /// <summary>NtFlushBuffersFile (or a write in write-through mode) on a handle with a write token.</summary>
    Flush = 6,
    /// <summary>Attributes or times set through a virtual handle (FileBasicInformation).</summary>
    SetInfo = 7,
    /// <summary>Rename or move inside the virtual folders.</summary>
    Rename = 8,
    /// <summary>Delete a virtual file or empty directory (disposition, delete-on-close, NtDeleteFile).</summary>
    Delete = 9,
}
