using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ProcessEntry
{
    [Key(0)]
    public int Pid { get; init; }

    /// <summary>Parent PID, 0 when it cannot be read (protected process, access denied).</summary>
    [Key(1)]
    public int ParentPid { get; init; }

    [Key(2)]
    public required string Name { get; init; }

    [Key(3)]
    public string? ImagePath { get; init; }

    [Key(4)]
    public int SessionId { get; init; }

    [Key(5)]
    public long WorkingSetBytes { get; init; }

    [Key(6)]
    public DateTime? StartTimeUtc { get; init; }
}
