using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Agent.Models;

/// <summary>Relay the Agent keeps a control channel to, so machines A outside the LAN reach it (plan 8.2).</summary>
public sealed record AgentRelayOptions
{
    /// <summary>Relay URL and this machine's device token.</summary>
    public required RelayEndpoint Endpoint { get; init; }

    /// <summary>WebSocket factory; null for the real <c>ClientWebSocket</c> (tests plug in an in-process relay).</summary>
    public IRelaySocketFactory? Sockets { get; init; }
}
