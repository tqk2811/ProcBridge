using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    public async Task<TunnelSessionVM> OpenSessionAsync([FromBody] OpenTunnelSessionRequestVM request, CancellationToken cancellationToken = default)
    {
        var deviceId = User.GetDeviceId();
        var share = await _access.EnsureCanUseShareAsync(deviceId, request.ShareId, cancellationToken);
        return await _sessions.OpenAsync(deviceId, share.Id, share.DeviceId, cancellationToken);
    }
}
