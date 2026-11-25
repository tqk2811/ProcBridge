using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class DriveEntry
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public string? Label { get; init; }

    [Key(2)]
    public string? DriveType { get; init; }

    [Key(3)]
    public string? Format { get; init; }

    [Key(4)]
    public long TotalBytes { get; init; }

    [Key(5)]
    public long FreeBytes { get; init; }

    [Key(6)]
    public bool IsReady { get; init; }
}
