using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>Audit log that drops every entry.</summary>
public sealed class NullExplorerAuditLog : IExplorerAuditLog
{
    public static readonly NullExplorerAuditLog Instance = new();

    private NullExplorerAuditLog()
    {
    }

    public void Write(ExplorerAuditEntry entry)
    {
    }
}
