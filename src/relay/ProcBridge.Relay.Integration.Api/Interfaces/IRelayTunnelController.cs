using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/tunnel</c> (anonymous; rate limited per client address).</summary>
public interface IRelayTunnelController
{
    /// <summary><c>POST api/tunnel/sessions</c>: open a session to a host by its HostId; then open lanes on <c>/tunnel</c>.</summary>
    Task<TunnelSessionVM> OpenSessionAsync(OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default);

    /// <summary>
    /// <c>POST api/tunnel/pairing-sessions</c>: find a host by the lookup key of its pairing invite code (single use) and
    /// open a session to it; 404 <c>PAIRING_SLOT_NOT_FOUND</c> when no slot matches.
    /// </summary>
    Task<TunnelPairingSessionVM> OpenPairingSessionAsync(OpenPairingSessionRequestVM request, CancellationToken cancellationToken = default);
}
