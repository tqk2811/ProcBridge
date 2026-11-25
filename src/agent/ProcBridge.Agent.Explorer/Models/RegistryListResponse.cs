using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class RegistryListResponse : ExplorerResponse
{
    /// <summary>Normalized key path (short hive name).</summary>
    [Key(2)]
    public string? Path { get; set; }

    [Key(3)]
    public IReadOnlyList<string> SubKeys { get; set; } = [];

    [Key(4)]
    public IReadOnlyList<RegistryValue> Values { get; set; } = [];
}
