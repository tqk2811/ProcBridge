using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ProcessKillRequest
{
    [Key(0)]
    public int Pid { get; init; }

    [Key(1)]
    public bool EntireTree { get; init; }
}
