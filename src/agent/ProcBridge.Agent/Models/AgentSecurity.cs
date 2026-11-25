using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Models;

/// <summary>Identity and trust of the Agent (plan section 9). The caller owns the key and the store.</summary>
public sealed record AgentSecurity
{
    /// <summary>Long-term identity key of machine B (loaded with <c>IdentityKeyStore</c>, DPAPI).</summary>
    public required IdentityKey Identity { get; init; }

    /// <summary>Machines A allowed to connect; pairing adds to it, revoking drops their connections.</summary>
    public required PairedMachineStore PairedMachines { get; init; }

    /// <summary>Accept pairing requests from the start; see <see cref="AgentServer.AllowPairing"/>.</summary>
    public bool AllowPairing { get; init; }

    /// <summary>Remote Explorer profile of each paired machine A; a machine without one may do nothing.</summary>
    public ExplorerPolicyStore ExplorerPolicies { get; init; } = new();

    /// <summary>Where Explorer write operations are logged with the machine that asked; null logs nothing.</summary>
    public IExplorerAuditLog? ExplorerAudit { get; init; }
}
