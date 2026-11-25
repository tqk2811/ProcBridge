using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Creates or replaces a value on an existing key.</summary>
[MessagePackObject]
public sealed class RegistrySetValueRequest
{
    [Key(0)]
    public required string Path { get; init; }

    [Key(1)]
    public required RegistryValue Value { get; init; }
}
