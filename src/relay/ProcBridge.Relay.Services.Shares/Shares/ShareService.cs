using Microsoft.Extensions.Logging;

using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Common.Presence.Interfaces;
using ProcBridge.Relay.Services.Shares.Interfaces;
using ProcBridge.Relay.ViewModels.Records;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Services.Shares;

public sealed class ShareService : IShareService
{
    private const int MaxNameLength = 128;

    private readonly IShareRepository _shares;
    private readonly IDeviceRepository _devices;
    private readonly IDevicePresence _presence;
    private readonly ITunnelTerminator _tunnels;
    private readonly ILogger<ShareService> _logger;

    public ShareService(IShareRepository shares, IDeviceRepository devices, IDevicePresence presence, ITunnelTerminator tunnels, ILogger<ShareService> logger)
    {
        _shares = shares;
        _devices = devices;
        _presence = presence;
        _tunnels = tunnels;
        _logger = logger;
    }

    public async Task<ShareVM> CreateAsync(CreateShareRequestVM request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, $"Tên chia sẻ 1-{MaxNameLength} ký tự.");
        var created = await _shares.CreateAsync(new NewShareRecordVM(request.DeviceId, name), cancellationToken);
        _logger.LogInformation("Đã tạo chia sẻ {ShareId} ({Name}) trên máy {DeviceId}.", created.Id, created.Name, created.DeviceId);
        return ToVM(created);
    }

    public async Task<IReadOnlyList<ShareVM>> GetAllByOwnerAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
        => (await _shares.GetAllByOwnerAsync(ownerAccountId, cancellationToken)).Select(ToVM).ToList();

    public async Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        if (await _shares.HardDeleteAsync(shareId, cancellationToken))
            _logger.LogInformation("Đã xoá chia sẻ {ShareId}.", shareId);
        _tunnels.DisconnectShare(shareId);
    }

    public async Task<ShareGrantVM> GrantAsync(Guid shareId, GrantShareRequestVM request, CancellationToken cancellationToken = default)
    {
        var grantee = await _devices.GetByIdAsync(request.DeviceId, cancellationToken);
        if (grantee is null)
            throw ServiceException.NotFound(RelayErrors.DeviceNotFound, "Không tìm thấy máy được cấp quyền.");
        if (grantee.RevokedAt is not null)
            throw ServiceException.Conflict(RelayErrors.DeviceRevoked, "Máy được cấp quyền đã bị thu hồi.");
        var grant = await _shares.EnsureGrantAsync(shareId, request.DeviceId, cancellationToken);
        _logger.LogInformation("Đã cấp quyền chia sẻ {ShareId} cho máy {DeviceId}.", shareId, request.DeviceId);
        return ToVM(grant);
    }

    public async Task<IReadOnlyList<ShareGrantVM>> GetGrantsAsync(Guid shareId, CancellationToken cancellationToken = default)
        => (await _shares.GetGrantsAsync(shareId, cancellationToken)).Select(ToVM).ToList();

    public async Task RevokeGrantAsync(Guid shareId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (!await _shares.DeleteGrantAsync(shareId, deviceId, cancellationToken))
            throw ServiceException.NotFound(RelayErrors.ShareGrantNotFound, "Máy này chưa được cấp quyền chia sẻ đó.");
        _logger.LogInformation("Đã thu hồi quyền chia sẻ {ShareId} của máy {DeviceId}.", shareId, deviceId);
        _tunnels.DisconnectShare(shareId, deviceId);
    }

    public async Task<IReadOnlyList<AccessibleShareVM>> GetAccessibleAsync(Guid clientDeviceId, CancellationToken cancellationToken = default)
        => (await _shares.GetAccessibleAsync(clientDeviceId, cancellationToken))
            .Select(x => new AccessibleShareVM(x.ShareId, x.Name, x.HostDeviceId, x.HostDeviceName, x.HostPublicKey, _presence.IsOnline(x.HostDeviceId)))
            .ToList();

    private static ShareVM ToVM(ShareRecordVM record) => new(record.Id, record.DeviceId, record.Name, record.CreatedAt);

    private static ShareGrantVM ToVM(ShareGrantRecordVM record) => new(record.ShareId, record.GranteeDeviceId, record.CreatedAt);
}
