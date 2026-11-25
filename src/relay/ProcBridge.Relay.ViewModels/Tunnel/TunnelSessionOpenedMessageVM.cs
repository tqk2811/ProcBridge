namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// Control message (JSON text frame) pushed to the host device on <c>/tunnel/control</c> when a client opens a session.
/// The host answers by opening its own lanes with <see cref="Ticket"/>.
/// </summary>
public sealed record TunnelSessionOpenedMessageVM(
    string Type,
    Guid SessionId,
    string Ticket,
    DateTime ExpiresAt,
    Guid ShareId,
    Guid ClientDeviceId,
    string? ClientPublicKey);
