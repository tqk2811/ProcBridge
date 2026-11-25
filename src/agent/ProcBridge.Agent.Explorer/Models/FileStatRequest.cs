using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class FileStatRequest
{
    [Key(0)]
    public required string Path { get; init; }
}
