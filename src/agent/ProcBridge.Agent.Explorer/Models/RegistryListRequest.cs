using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class RegistryListRequest
{
    /// <summary>Key path such as <c>HKCU\Software\Vendor</c> (long hive names are accepted too).</summary>
    [Key(0)]
    public required string Path { get; init; }
}
