using ProcBridge.Relay.Services.Devices.Models;
using ProcBridge.Relay.ViewModels.Devices;

namespace ProcBridge.Relay.Services.Devices.Interfaces;

/// <summary>Device management. Does NOT check permissions: the caller passes the owner and checks access first.</summary>
public interface IDeviceService
{
    Task<DeviceRegisteredVM> RegisterAsync(Guid ownerAccountId, RegisterDeviceRequestVM request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeviceVM>> GetAllAsync(Guid ownerAccountId, CancellationToken cancellationToken = default);
    Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default);
    /// <summary><c>null</c> when the token is unknown or the device is revoked.</summary>
    Task<DeviceIdentity?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);
}
