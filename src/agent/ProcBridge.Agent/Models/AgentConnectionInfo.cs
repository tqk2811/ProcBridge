namespace ProcBridge.Agent.Models;

/// <summary>One open connection of the Agent, for display.</summary>
public sealed record AgentConnectionInfo
{
    /// <summary>Remote endpoint (<c>host:port</c>), not authenticated.</summary>
    public required string RemoteAddress { get; init; }

    /// <summary>Fingerprint of the authenticated machine A; null before the handshake or on a plain connection.</summary>
    public string? PeerFingerprint { get; init; }

    public DateTime ConnectedAtUtc { get; init; }
}
