namespace ProcBridge.Agent.Explorer.Enums;

/// <summary>Result code carried by every Explorer response.</summary>
public enum ExplorerStatus
{
    Ok = 0,
    /// <summary>The permission profile does not allow this kind of operation.</summary>
    PermissionDenied = 1,
    /// <summary>The target is outside the roots the profile allows (traversal, link escape, other hive...).</summary>
    OutOfScope = 2,
    NotFound = 3,
    InvalidArgument = 4,
    /// <summary>Windows refused the operation for the account the Agent runs as.</summary>
    AccessDenied = 5,
    AlreadyExists = 6,
    Failed = 7,
}
