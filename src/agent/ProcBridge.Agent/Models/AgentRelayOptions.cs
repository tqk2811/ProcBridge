using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Agent.Models;

/// <summary>
/// Relay the Agent keeps a control channel to, so machines A outside the LAN reach it (plan 8.2). Only the URL: the
/// Agent proves its HostId (fingerprint of its identity key) with a Noise handshake, the relay keeps no accounts.
/// </summary>
public sealed record AgentRelayOptions
{
    /// <summary>Relay URL.</summary>
    public required RelayEndpoint Endpoint { get; init; }

    /// <summary>WebSocket factory; null for the real <c>ClientWebSocket</c> (tests plug in an in-process relay).</summary>
    public IRelaySocketFactory? Sockets { get; init; }
}
