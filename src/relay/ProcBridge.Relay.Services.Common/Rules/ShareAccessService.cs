using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Services.Common.Rules;

/// <summary>
/// Permission gates of the relay. Controllers call these BEFORE the business service; business services do not check
/// permissions themselves.
/// </summary>
public sealed class ShareAccessService
{
    private readonly IDeviceRepository _devices;
    private readonly IShareRepository _shares;

    public ShareAccessService(IDeviceRepository devices, IShareRepository shares)
    {
        _devices = devices;
        _shares = shares;
    }

    /// <summary>The device exists, is not revoked and belongs to the account; otherwise 404 (do not leak other accounts' ids).</summary>
    public async Task<DeviceRecordVM> EnsureDeviceOwnerAsync(Guid accountId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _devices.GetByIdAsync(deviceId, cancellationToken);
        if (device is null || device.OwnerAccountId != accountId)
            throw ServiceException.NotFound(RelayErrors.DeviceNotFound, "Không tìm thấy máy.");
        if (device.RevokedAt is not null)
            throw ServiceException.Conflict(RelayErrors.DeviceRevoked, "Máy đã bị thu hồi.");
        return device;
    }

    /// <summary>The share exists and its host device belongs to the account; otherwise 404.</summary>
    public async Task<ShareRecordVM> EnsureShareOwnerAsync(Guid accountId, Guid shareId, CancellationToken cancellationToken = default)
    {
        var share = await _shares.GetByIdAsync(shareId, cancellationToken);
        if (share is null || share.OwnerAccountId != accountId)
            throw ServiceException.NotFound(RelayErrors.ShareNotFound, "Không tìm thấy chia sẻ.");
        return share;
    }

    /// <summary>
    /// The client device may open tunnel sessions to the share: it holds a grant, and the host device is not revoked.
    /// Missing share and missing grant answer the same 403 so a device cannot probe which share ids exist.
    /// </summary>
    public async Task<ShareRecordVM> EnsureCanUseShareAsync(Guid clientDeviceId, Guid shareId, CancellationToken cancellationToken = default)
        => await FindUsableShareAsync(clientDeviceId, shareId, cancellationToken)
            ?? throw ServiceException.Forbidden(RelayErrors.ShareAccessDenied, "Máy này không được dùng chia sẻ đó.");

    /// <summary>The same rule as <see cref="EnsureCanUseShareAsync"/> as a yes/no, for re-checks after a possible revocation.</summary>
    public async Task<bool> CanUseShareAsync(Guid clientDeviceId, Guid shareId, CancellationToken cancellationToken = default)
        => await FindUsableShareAsync(clientDeviceId, shareId, cancellationToken) is not null;

    private async Task<ShareRecordVM?> FindUsableShareAsync(Guid clientDeviceId, Guid shareId, CancellationToken cancellationToken)
    {
        var share = await _shares.GetByIdAsync(shareId, cancellationToken);
        if (share is null || !await _shares.GrantExistsAsync(shareId, clientDeviceId, cancellationToken))
            return null;
        var host = await _devices.GetByIdAsync(share.DeviceId, cancellationToken);
        return host is null || host.RevokedAt is not null ? null : share;
    }
}
