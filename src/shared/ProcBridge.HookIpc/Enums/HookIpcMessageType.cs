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
}
