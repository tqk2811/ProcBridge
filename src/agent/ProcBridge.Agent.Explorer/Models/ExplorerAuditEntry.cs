using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

public sealed class ExplorerAuditEntry
{
    public required DateTime TimeUtc { get; init; }
    /// <summary>Identifier of the paired machine A that sent the request.</summary>
    public required string RequesterId { get; init; }
    public required string Action { get; init; }
    public required string Target { get; init; }
    public required ExplorerStatus Status { get; init; }
}
