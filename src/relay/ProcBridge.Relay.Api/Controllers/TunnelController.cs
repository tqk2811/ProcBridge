using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Api.Controllers;

/// <summary>
/// Opens tunnel sessions. Anonymous: the relay has no accounts and grants nothing; the Noise IK handshake end to end
/// inside the lanes decides who talks to whom. Rate limited and capped per client address and per HostId.
/// </summary>
[ApiController]
[Route("api/tunnel")]
public sealed class TunnelController : ControllerBase, IRelayTunnelController
{
    private readonly ITunnelSessionService _sessions;

    public TunnelController(ITunnelSessionService sessions) => _sessions = sessions;

    [HttpPost("sessions")]
    [EnableRateLimiting(RelayRateLimitOptions.TunnelSessionPolicyName)]
    public Task<TunnelSessionVM> OpenSessionAsync([FromBody] OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default)
        => _sessions.OpenAsync(request.HostId, HttpContext.ClientAddress(), cancellationToken);
}
