namespace ProcBridge.Relay.ViewModels.Tunnel;

public sealed record OpenTunnelSessionRequestVM
{
    public required Guid ShareId { get; init; }
}
