using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Broker.Models;

/// <summary>Reach the Agent through a relay instead of TCP on the LAN (plan 8.2). Needs <see cref="BrokerSecurity"/>.</summary>
public sealed record BrokerRelayOptions
{
    /// <summary>Relay URL and this machine's device token.</summary>
    public required RelayEndpoint Endpoint { get; init; }

    /// <summary>Relay share id (from <c>procbridge-relay device shares</c>) that the Agent serves.</summary>
    public required Guid ShareId { get; init; }

    /// <summary>WebSocket factory; null for the real <c>ClientWebSocket</c> (tests plug in an in-process relay).</summary>
    public IRelaySocketFactory? Sockets { get; init; }

    /// <summary>HTTP client for the REST call, not owned; null creates one on the relay URL.</summary>
    public HttpClient? Http { get; init; }
}
