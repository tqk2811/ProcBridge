using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ServiceListResponse : ExplorerResponse
{
    [Key(2)]
    public IReadOnlyList<ServiceEntry> Services { get; set; } = [];
}
