using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Integration.Api.Interfaces;

/// <summary>Group <c>api/devices</c>.</summary>
public interface IRelayDevicesController
{
    /// <summary><c>POST api/devices</c> (account token). The returned device token is shown only once.</summary>
    Task<DeviceRegisteredVM> RegisterAsync(RegisterDeviceRequestVM request, CancellationToken cancellationToken = default);

    /// <summary><c>GET api/devices</c> (account token).</summary>
    Task<IReadOnlyList<DeviceVM>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary><c>DELETE api/devices/{deviceId}</c> (account token): revoke the device token.</summary>
    Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary><c>GET api/devices/me/shares</c> (device token): shares this device may open sessions to.</summary>
    Task<IReadOnlyList<AccessibleShareVM>> GetAccessibleSharesAsync(CancellationToken cancellationToken = default);
}
