using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;

namespace ProcBridge.Relay.Integration.Api.Implements;

public sealed partial class RelayApiImplement
{
    private sealed class DevicesControllerImplement : IRelayDevicesController
    {
        private const string BaseUrl = "api/devices";
        private readonly RelayApiImplement _api;

        public DevicesControllerImplement(RelayApiImplement api) => _api = api;

        public Task<DeviceRegisteredVM> RegisterAsync(RegisterDeviceRequestVM request, CancellationToken cancellationToken = default)
            => _api.SendAsync<DeviceRegisteredVM>(HttpMethod.Post, BaseUrl, request, cancellationToken);

        public async Task<IReadOnlyList<DeviceVM>> GetAllAsync(CancellationToken cancellationToken = default)
            => await _api.SendAsync<List<DeviceVM>>(HttpMethod.Get, BaseUrl, null, cancellationToken);

        public Task RevokeAsync(Guid deviceId, CancellationToken cancellationToken = default)
            => _api.SendVoidAsync(HttpMethod.Delete, $"{BaseUrl}/{deviceId}", null, cancellationToken);

        public async Task<IReadOnlyList<AccessibleShareVM>> GetAccessibleSharesAsync(CancellationToken cancellationToken = default)
            => await _api.SendAsync<List<AccessibleShareVM>>(HttpMethod.Get, $"{BaseUrl}/me/shares", null, cancellationToken);
    }
}
