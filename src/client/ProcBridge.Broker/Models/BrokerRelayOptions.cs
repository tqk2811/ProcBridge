using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Broker.Models;

/// <summary>
/// Reach the Agent through a relay instead of TCP on the LAN (plan 8.2). Needs <see cref="BrokerSecurity"/>: the
/// session goes to the HostId of <see cref="BrokerSecurity.AgentPublicKey"/> (the paired Agent), and the Noise IK
/// handshake inside every lane is what authenticates both ends.
/// </summary>
public sealed record BrokerRelayOptions
{
    /// <summary>Relay URL.</summary>
    public required RelayEndpoint Endpoint { get; init; }

    /// <summary>WebSocket factory; null for the real <c>ClientWebSocket</c> (tests plug in an in-process relay).</summary>
    public IRelaySocketFactory? Sockets { get; init; }

    /// <summary>HTTP client for the REST call, not owned; null creates one on the relay URL.</summary>
    public HttpClient? Http { get; init; }
}
