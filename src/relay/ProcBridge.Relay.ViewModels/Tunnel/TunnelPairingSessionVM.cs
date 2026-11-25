namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// A tunnel session opened through a pairing slot: like <see cref="TunnelSessionVM"/>, plus the HostId the slot belonged
/// to. Machine A checks it against the fingerprint the pairing handshake proves (a relay that routes elsewhere is caught).
/// </summary>
public sealed record TunnelPairingSessionVM(Guid SessionId, string Ticket, DateTime ExpiresAt, string HostId);
