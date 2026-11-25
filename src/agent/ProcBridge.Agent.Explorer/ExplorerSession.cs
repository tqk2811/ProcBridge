using ProcBridge.Agent.Explorer.Enums;
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

        _audit = log;
        Policy = policy;
        RequesterId = requesterId;
        Files = new FileExplorerProvider(policy, requesterId, log);
        Processes = new ProcessExplorerProvider(policy, requesterId, log);
        Registry = new RegistryExplorerProvider(policy, requesterId, log);
        Services = new ServiceExplorerProvider(policy, requesterId, log);
        SystemInfo = new SystemInfoProvider(policy, requesterId, log);
    }

    private readonly IExplorerAuditLog _audit;

    public ExplorerPolicy Policy { get; }

    /// <summary>
    /// The shares for this machine A, logged in the audit. The folder on machine B is given only when the profile lets it
    /// browse that folder anyway (<see cref="ExplorerPermissions.FileRead"/> and the folder inside a file root); otherwise
    /// only the name and the write flag, which any paired machine learns by opening the share.
    /// </summary>
    public ShareListResponse ListShares(IReadOnlyList<ShareEntry> shares)
    {
        ShareEntry[] visible = shares
            .Select(x => CanBrowse(x.RootPath) ? x : x with { RootPath = string.Empty })
            .ToArray();
        _audit.Write(new ExplorerAuditEntry
        {
            TimeUtc = DateTime.UtcNow,
            RequesterId = RequesterId,
            Action = "ListShares",
            Target = string.Join(", ", visible.Select(x => x.Name)),
            Status = ExplorerStatus.Ok,
        });
        return new ShareListResponse { Shares = visible };
    }

    private bool CanBrowse(string folder)
    {
        if (!Policy.Permissions.HasFlag(ExplorerPermissions.FileRead))
            return false;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return Policy.FileRoots.Any(root =>
        {
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return string.Equals(full, r, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(r.EndsWith('\\') ? r : r + @"\", StringComparison.OrdinalIgnoreCase);
        });
    }

    public string RequesterId { get; }

    public FileExplorerProvider Files { get; }

    public ProcessExplorerProvider Processes { get; }

    public RegistryExplorerProvider Registry { get; }

    public ServiceExplorerProvider Services { get; }

    public SystemInfoProvider SystemInfo { get; }
}
