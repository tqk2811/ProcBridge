using MessagePack;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>One share of the Agent: the name used in <c>Hello</c> and its folder on machine B.</summary>
[MessagePackObject]
public sealed record ShareEntry
{
    [Key(0)]
    public required string Name { get; init; }

    /// <summary>Absolute folder on machine B; paths of the <c>Fs.*</c> messages are relative to it.</summary>
    [Key(1)]
    public required string RootPath { get; init; }

    /// <summary>Machine A may write back into it.</summary>
    [Key(2)]
    public bool Writable { get; init; }
}
