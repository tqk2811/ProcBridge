namespace ProcBridge.Protocol.Enums;

/// <summary>Kind of one change on machine B. Values match Win32 <c>FILE_ACTION_*</c> so the hook can pass them on.</summary>
public enum FsChangeAction : uint
{
    Added = 1,
    Removed = 2,
    Modified = 3,
    RenamedOldName = 4,
    RenamedNewName = 5,
}
