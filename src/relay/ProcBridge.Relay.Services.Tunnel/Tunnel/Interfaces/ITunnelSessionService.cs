using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>Opens tunnel sessions. Does NOT check the share grant: the controller calls <c>ShareAccessService</c> first.</summary>
public interface ITunnelSessionService
{
    /// <summary>See <see cref="ITunnelHub.RevocationEpoch"/>: read it before the permission check.</summary>
    long RevocationEpoch { get; }

    /// <param name="access">Re-check of the permission for the case a revocation raced the caller's check; null skips it.</param>
    Task<TunnelSessionVM> OpenAsync(Guid clientDeviceId, Guid shareId, Guid hostDeviceId, CancellationToken cancellationToken = default, TunnelAccessCheck? access = null);
}
