using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ProcessListResponse : ExplorerResponse
{
    [Key(2)]
    public IReadOnlyList<ProcessEntry> Processes { get; set; } = [];
}
