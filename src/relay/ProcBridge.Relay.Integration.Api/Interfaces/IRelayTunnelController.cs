using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/tunnel</c> (device token).</summary>
public interface IRelayTunnelController
{
    /// <summary><c>POST api/tunnel/sessions</c>: open a session to a granted share; then open lanes on <c>/tunnel</c>.</summary>
    Task<TunnelSessionVM> OpenSessionAsync(OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default);
}
