using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Tests.Explorer;

internal sealed class RecordingAuditLog : IExplorerAuditLog
{
    private readonly List<ExplorerAuditEntry> _entries = [];

    public IReadOnlyList<ExplorerAuditEntry> Entries => _entries;

    public void Write(ExplorerAuditEntry entry) => _entries.Add(entry);
}
