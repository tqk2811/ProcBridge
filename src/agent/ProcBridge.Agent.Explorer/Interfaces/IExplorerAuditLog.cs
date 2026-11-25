using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer.Interfaces;

/// <summary>Receives one entry for every write operation a machine A requested (allowed or not).</summary>
public interface IExplorerAuditLog
{
    void Write(ExplorerAuditEntry entry);
}
