namespace ProcBridge.Agent.Explorer.Enums;

/// <summary>Registry value type, mirrors the Win32 REG_* types the Explorer supports.</summary>
public enum RegistryValueType
{
    Unknown = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    MultiString = 5,
    QWord = 6,
    None = 7,
}
