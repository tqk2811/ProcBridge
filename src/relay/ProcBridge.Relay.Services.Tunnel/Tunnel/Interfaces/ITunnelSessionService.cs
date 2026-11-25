using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>Opens tunnel sessions to a host by its HostId. The relay grants nothing: Noise IK end to end authenticates both ends.</summary>
public interface ITunnelSessionService
{
    /// <param name="hostId">Fingerprint of the host's identity key.</param>
    /// <param name="clientAddress">Address of the caller, for the per-address session limit.</param>
    Task<TunnelSessionVM> OpenAsync(string hostId, string clientAddress, CancellationToken cancellationToken = default);
}
