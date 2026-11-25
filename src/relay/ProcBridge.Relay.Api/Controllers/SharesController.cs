using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using ProcBridge.Relay.Api.Extensions;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Common.Rules;
using ProcBridge.Relay.Services.Shares.Interfaces;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Api.Controllers;

[ApiController]
[Route("api/shares")]
[Authorize(Policy = RelayAuth.AccountPolicy)]
public sealed class SharesController : ControllerBase, IRelaySharesController
{
    private readonly IShareService _shares;
    private readonly ShareAccessService _access;

    public SharesController(IShareService shares, ShareAccessService access)
    {
        _shares = shares;
        _access = access;
    }

    [HttpPost]
    public async Task<ShareVM> CreateAsync([FromBody] CreateShareRequestVM request, CancellationToken cancellationToken = default)
    {
        await _access.EnsureDeviceOwnerAsync(User.GetAccountId(), request.DeviceId, cancellationToken);
        return await _shares.CreateAsync(request, cancellationToken);
    }

    [HttpGet]
    public Task<IReadOnlyList<ShareVM>> GetAllAsync(CancellationToken cancellationToken = default)
        => _shares.GetAllByOwnerAsync(User.GetAccountId(), cancellationToken);

    [HttpDelete("{shareId:guid}")]
    public async Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        await _access.EnsureShareOwnerAsync(User.GetAccountId(), shareId, cancellationToken);
        await _shares.DeleteAsync(shareId, cancellationToken);
    }

    [HttpPost("{shareId:guid}/grants")]
    public async Task<ShareGrantVM> GrantAsync(Guid shareId, [FromBody] GrantShareRequestVM request, CancellationToken cancellationToken = default)
    {
        await _access.EnsureShareOwnerAsync(User.GetAccountId(), shareId, cancellationToken);
        return await _shares.GrantAsync(shareId, request, cancellationToken);
    }

    [HttpGet("{shareId:guid}/grants")]
    public async Task<IReadOnlyList<ShareGrantVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        await _access.EnsureShareOwnerAsync(User.GetAccountId(), shareId, cancellationToken);
        return await _shares.GetGrantsAsync(shareId, cancellationToken);
    }

    [HttpDelete("{shareId:guid}/grants/{deviceId:guid}")]
    public async Task RevokeGrantAsync(Guid shareId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        await _access.EnsureShareOwnerAsync(User.GetAccountId(), shareId, cancellationToken);
        await _shares.RevokeGrantAsync(shareId, deviceId, cancellationToken);
    }
}
