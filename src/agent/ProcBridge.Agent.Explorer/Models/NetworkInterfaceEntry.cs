using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class NetworkInterfaceEntry
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public string? Description { get; init; }

    [Key(2)]
    public IReadOnlyList<string> Addresses { get; set; } = [];
}
