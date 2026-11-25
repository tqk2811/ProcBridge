using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class RegistryDeleteValueRequest
{
    [Key(0)]
    public required string Path { get; init; }

    [Key(1)]
    public required string Name { get; init; }
}
