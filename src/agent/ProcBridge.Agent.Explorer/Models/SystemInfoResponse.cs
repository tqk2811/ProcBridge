using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class SystemInfoResponse : ExplorerResponse
{
    [Key(2)]
    public string? MachineName { get; set; }

    [Key(3)]
    public string? OsDescription { get; set; }

    [Key(4)]
    public string? OsVersion { get; set; }

    [Key(5)]
    public bool Is64BitOs { get; set; }

    [Key(6)]
    public int ProcessorCount { get; set; }

    [Key(7)]
    public long TotalMemoryBytes { get; set; }

    /// <summary>Account the Agent runs as (domain\user).</summary>
    [Key(8)]
    public string? AgentUser { get; set; }

    [Key(9)]
    public long UptimeMilliseconds { get; set; }

    [Key(10)]
    public IReadOnlyList<NetworkInterfaceEntry> Networks { get; set; } = [];
}
