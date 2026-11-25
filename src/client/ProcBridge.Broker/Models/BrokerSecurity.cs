using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Broker.Models;

/// <summary>Identity and trust of machine A for one Agent (plan section 9). The caller owns the key and the store.</summary>
public sealed record BrokerSecurity
{
    /// <summary>Long-term identity key of machine A (loaded with <c>IdentityKeyStore</c>, DPAPI).</summary>
    public required IdentityKey Identity { get; init; }

    /// <summary>Agents this machine paired with; the Agent must be in it at every handshake.</summary>
    public required PairedMachineStore PairedMachines { get; init; }

    /// <summary>Identity public key of the Agent, from pairing (<c>PairedMachine.PublicKey</c>).</summary>
    public required byte[] AgentPublicKey { get; init; }
}
