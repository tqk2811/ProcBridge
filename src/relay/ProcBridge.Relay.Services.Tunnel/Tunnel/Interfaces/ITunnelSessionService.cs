using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>Opens tunnel sessions. Does NOT check the share grant: the controller calls <c>ShareAccessService</c> first.</summary>
public interface ITunnelSessionService
{
    Task<TunnelSessionVM> OpenAsync(Guid clientDeviceId, Guid shareId, Guid hostDeviceId, CancellationToken cancellationToken = default);
}
