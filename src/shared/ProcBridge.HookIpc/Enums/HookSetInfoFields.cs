namespace ProcBridge.HookIpc.Enums;

/// <summary>Which fields of a SetInfo request are set.</summary>
[Flags]
public enum HookSetInfoFields : ushort
{
    None = 0,
    Attributes = 1,
    CreationTime = 2,
    LastAccessTime = 4,
    LastWriteTime = 8,
    EndOfFile = 16,
}
