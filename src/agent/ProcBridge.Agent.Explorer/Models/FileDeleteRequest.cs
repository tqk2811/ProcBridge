using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class FileDeleteRequest
{
    [Key(0)]
    public required string Path { get; init; }

    /// <summary>Delete a non empty directory with its content. Links inside are removed, not followed.</summary>
    [Key(1)]
    public bool Recursive { get; init; }
}
