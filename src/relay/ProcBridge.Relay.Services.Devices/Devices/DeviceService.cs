using Microsoft.Extensions.Logging;

using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Common.Presence.Interfaces;
using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Services.Devices.Interfaces;
using ProcBridge.Relay.Services.Devices.Models;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Records;

namespace ProcBridge.Relay.Services.Devices;

public sealed class DeviceService : IDeviceService
{
    private const int MaxNameLength = 128;
    private const int MaxPublicKeyLength = 512;

    private readonly IDeviceRepository _devices;
    private readonly IDevicePresence _presence;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceService> _logger;

    public DeviceService(IDeviceRepository devices, IDevicePresence presence, TimeProvider time, ILogger<DeviceService> logger)
    {
        _devices = devices;
        _presence = presence;
        _time = time;
        _logger = logger;
    }

    public async Task<DeviceRegisteredVM> RegisterAsync(Guid ownerAccountId, RegisterDeviceRequestVM request, CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength)
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, $"Tên máy 1-{MaxNameLength} ký tự.");
        var publicKey = string.IsNullOrWhiteSpace(request.PublicKey) ? null : request.PublicKey.Trim();
        if (publicKey is not null && publicKey.Length > MaxPublicKeyLength)
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, $"Khoá công khai tối đa {MaxPublicKeyLength} ký tự.");

        var token = SecretTokens.Create(RelayAuth.DeviceTokenPrefix);
        var created = await _devices.CreateAsync(new NewDeviceRecordVM(ownerAccountId, name, publicKey, SecretTokens.Hash(token)), cancellationToken);
        _logger.LogInformation("Đã đăng ký máy {DeviceId} ({Name}) cho tài khoản {AccountId}.", created.Id, created.Name, ownerAccountId);
        return new DeviceRegisteredVM(ToVM(created), token);
    }

    public async Task<IReadOnlyList<DeviceVM>> GetAllAsync(Guid ownerAccountId, CancellationToken cancellationToken = default)
        => (await _devices.GetAllByOwnerAsync(ownerAccountId, cancellationToken)).Select(ToVM).ToList();

    public async Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (await _devices.RevokeAsync(deviceId, _time.GetUtcNow().UtcDateTime, cancellationToken))
            _logger.LogInformation("Đã thu hồi máy {DeviceId}.", deviceId);
    }

    public async Task<DeviceIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (!token.StartsWith(RelayAuth.DeviceTokenPrefix, StringComparison.Ordinal))
            return null;
        var device = await _devices.GetByTokenHashAsync(SecretTokens.Hash(token), cancellationToken);
        if (device is null || device.RevokedAt is not null)
            return null;
        return new DeviceIdentity(device.Id, device.OwnerAccountId);
    }

    private DeviceVM ToVM(DeviceRecordVM record)
        => new(record.Id, record.Name, record.PublicKey, record.CreatedAt, record.RevokedAt is not null, record.RevokedAt is null && _presence.IsOnline(record.Id));
}
