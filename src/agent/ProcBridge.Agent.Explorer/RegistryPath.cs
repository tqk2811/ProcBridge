using Microsoft.Win32;

namespace ProcBridge.Agent.Explorer;

/// <summary>A checked registry key path: hive, sub key below it, and the normalized text (short hive name).</summary>
internal readonly record struct RegistryPath(RegistryHive Hive, string SubKey, string Normalized)
{
    public RegistryKey OpenBase() => RegistryKey.OpenBaseKey(Hive, RegistryView.Default);
}
