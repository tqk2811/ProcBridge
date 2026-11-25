namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// Control message (JSON text frame) pushed to the host device on <c>/tunnel/control</c> when the client end of a lane
/// is waiting and the host end is not attached yet. The host answers by opening that lane with the ticket of the
/// session (from <see cref="TunnelSessionOpenedMessageVM"/>).
/// </summary>
public sealed record TunnelLaneRequestedMessageVM(string Type, Guid SessionId, int Lane);
