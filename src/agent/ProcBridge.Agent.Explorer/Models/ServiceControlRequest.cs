using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

[MessagePackObject]
public sealed class ServiceControlRequest
{
    [Key(0)]
    public required string Name { get; init; }

    [Key(1)]
    public required ServiceControlAction Action { get; init; }
}
