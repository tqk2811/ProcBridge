using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Common.Rules;
using ProcBridge.Relay.Services.Devices.Interfaces;
using ProcBridge.Relay.Services.Shares.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Api.Controllers;

[ApiController]
[Route("api/devices")]
public sealed class DevicesController : ControllerBase, IRelayDevicesController
{
    private readonly IDeviceService _devices;
    private readonly IShareService _shares;
    private readonly ShareAccessService _access;

    public DevicesController(IDeviceService devices, IShareService shares, ShareAccessService access)
    {
        _devices = devices;
        _shares = shares;
        _access = access;
    }

    [HttpPost]
    [Authorize(Policy = RelayAuth.AccountPolicy)]
    public Task<DeviceRegisteredVM> RegisterAsync([FromBody] RegisterDeviceRequestVM request, CancellationToken cancellationToken = default)
        => _devices.RegisterAsync(User.GetAccountId(), request, cancellationToken);

    [HttpGet]
    [Authorize(Policy = RelayAuth.AccountPolicy)]
    public Task<IReadOnlyList<DeviceVM>> GetAllAsync(CancellationToken cancellationToken = default)
        => _devices.GetAllAsync(User.GetAccountId(), cancellationToken);

    [HttpDelete("{deviceId:guid}")]
    [Authorize(Policy = RelayAuth.AccountPolicy)]
    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _access.EnsureDeviceOwnerAsync(User.GetAccountId(), deviceId, cancellationToken);
        await _devices.RevokeAsync(deviceId, cancellationToken);
    }

    [HttpGet("me/shares")]
    [Authorize(Policy = RelayAuth.DevicePolicy)]
    public Task<IReadOnlyList<AccessibleShareVM>> GetAccessibleSharesAsync(CancellationToken cancellationToken = default)
        => _shares.GetAccessibleAsync(User.GetDeviceId(), cancellationToken);
}
