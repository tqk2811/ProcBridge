using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

using ProcBridge.Relay.Api.Infrastructure;
using ProcBridge.Relay.Services.Tunnel.Models;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Common.Rules;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Api.Controllers;

[ApiController]
[Route("api/tunnel")]
[Authorize(Policy = RelayAuth.DevicePolicy)]
public sealed class TunnelController : ControllerBase, IRelayTunnelController
{
    private readonly ITunnelSessionService _sessions;
    private readonly ShareAccessService _access;

    public TunnelController(ITunnelSessionService sessions, ShareAccessService access)
    {
        _sessions = sessions;
        _access = access;
    }

    [HttpPost("sessions")]
    [EnableRateLimiting(AuthRateLimitOptions.TunnelSessionPolicyName)]
    public async Task<TunnelSessionVM> OpenSessionAsync([FromBody] OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default)
    {
        var deviceId = User.GetDeviceId();
        // The epoch comes BEFORE the permission check: a revocation that lands between the check and the hub
        // registering the session moves it, and the hub then re-checks (see TunnelAccessCheck).
        var epoch = _sessions.RevocationEpoch;
        var share = await _access.EnsureCanUseShareAsync(deviceId, request.ShareId, cancellationToken);
        var access = new TunnelAccessCheck(epoch, ct => _access.CanUseShareAsync(deviceId, request.ShareId, ct));
        return await _sessions.OpenAsync(deviceId, share.Id, share.DeviceId, cancellationToken, access);
    }
}
