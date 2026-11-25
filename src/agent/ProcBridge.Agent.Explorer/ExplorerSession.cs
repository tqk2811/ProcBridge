using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>
/// Remote Explorer entry point for one paired machine A. Holds the permission profile machine B
/// granted it and exposes one provider per resource kind. Every request outside the profile is
/// refused with a status, never thrown.
/// </summary>
/// <remarks>The audit log is supplied by the caller and is not owned by the session.</remarks>
public sealed class ExplorerSession
{
    public ExplorerSession(ExplorerPolicy policy, string requesterId, IExplorerAuditLog? audit = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(requesterId);
        var log = audit ?? NullExplorerAuditLog.Instance;

        Policy = policy;
        RequesterId = requesterId;
        Files = new FileExplorerProvider(policy, requesterId, log);
        Processes = new ProcessExplorerProvider(policy, requesterId, log);
        Registry = new RegistryExplorerProvider(policy, requesterId, log);
        Services = new ServiceExplorerProvider(policy, requesterId, log);
        SystemInfo = new SystemInfoProvider(policy, requesterId, log);
    }

    public ExplorerPolicy Policy { get; }

    public string RequesterId { get; }

    public FileExplorerProvider Files { get; }

    public ProcessExplorerProvider Processes { get; }

    public RegistryExplorerProvider Registry { get; }

    public ServiceExplorerProvider Services { get; }

    public SystemInfoProvider SystemInfo { get; }
}
