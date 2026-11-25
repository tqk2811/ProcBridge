using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel;

public sealed class TunnelSessionService : ITunnelSessionService
{
    private readonly IDeviceRepository _devices;
    private readonly ITunnelHub _hub;

    public TunnelSessionService(IDeviceRepository devices, ITunnelHub hub)
    {
        _devices = devices;
        _hub = hub;
    }

    public long RevocationEpoch => _hub.RevocationEpoch;

    public async Task<TunnelSessionVM> OpenAsync(Guid clientDeviceId, Guid shareId, Guid hostDeviceId, CancellationToken cancellationToken = default, TunnelAccessCheck? access = null)
    {
        var client = await _devices.GetByIdAsync(clientDeviceId, cancellationToken)
            ?? throw ServiceException.NotFound(RelayErrors.DeviceNotFound, "Không tìm thấy máy.");
        var host = await _devices.GetByIdAsync(hostDeviceId, cancellationToken)
            ?? throw ServiceException.NotFound(RelayErrors.DeviceNotFound, "Không tìm thấy máy.");
        // Public keys travel with the session so each end can check the other's fingerprint (decision in Plan 9).
        return await _hub.OpenSessionAsync(clientDeviceId, client.PublicKey, shareId, hostDeviceId, host.PublicKey, cancellationToken, access);
    }
}
