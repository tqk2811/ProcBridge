using ProcBridge.Agent.Models;
using ProcBridge.Broker.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Tests.Secure;

/// <summary>
/// Identity keys of a machine A (Broker) and a machine B (Agent) with in-memory paired machine stores; by default
/// each store already holds the other machine, as after a pairing. Owns both keys.
/// </summary>
internal sealed class SecurePeers : IDisposable
{
    public SecurePeers(bool paired = true)
    {
        if (paired)
        {
            AgentTrust.Add(BrokerKey.PublicKey, "machine-a");
            BrokerTrust.Add(AgentKey.PublicKey, "machine-b", "127.0.0.1");
        }
    }

    public IdentityKey AgentKey { get; } = IdentityKey.Generate();

    public IdentityKey BrokerKey { get; } = IdentityKey.Generate();

    /// <summary>Machines A the Agent accepts.</summary>
    public PairedMachineStore AgentTrust { get; } = new();

    /// <summary>Agents machine A trusts.</summary>
    public PairedMachineStore BrokerTrust { get; } = new();

    public AgentSecurity Agent(bool allowPairing = false) => new() { Identity = AgentKey, PairedMachines = AgentTrust, AllowPairing = allowPairing };

    public BrokerSecurity Broker => new() { Identity = BrokerKey, PairedMachines = BrokerTrust, AgentPublicKey = AgentKey.PublicKey.ToArray() };

    public void Dispose()
    {
        AgentKey.Dispose();
        BrokerKey.Dispose();
    }
}
