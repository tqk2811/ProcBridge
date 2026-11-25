using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/tunnel</c> (anonymous; rate limited per client address).</summary>
public interface IRelayTunnelController
{
    /// <summary><c>POST api/tunnel/sessions</c>: open a session to a host by its HostId; then open lanes on <c>/tunnel</c>.</summary>
    Task<TunnelSessionVM> OpenSessionAsync(OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default);
}
