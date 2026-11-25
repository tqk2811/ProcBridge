namespace ProcBridge.Agent.Explorer.Enums;

/// <summary>
/// Permission flags granted by the owner of machine B to one paired machine A.
/// Every write operation has its own flag and none is granted by default.
/// </summary>
[Flags]
public enum ExplorerPermissions
{
    None = 0,
    FileRead = 1 << 0,
    FileWrite = 1 << 1,
    ProcessRead = 1 << 2,
    ProcessKill = 1 << 3,
    RegistryRead = 1 << 4,
    RegistryWrite = 1 << 5,
    ServiceRead = 1 << 6,
    ServiceControl = 1 << 7,
    SystemInfo = 1 << 8,
}
