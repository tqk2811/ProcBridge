using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel.Interfaces;

/// <summary>Opens tunnel sessions to a host by its HostId. The relay grants nothing: Noise IK end to end authenticates both ends.</summary>
public interface ITunnelSessionService
{
    /// <param name="hostId">Fingerprint of the host's identity key.</param>
    /// <param name="clientAddress">Address of the caller, for the per-address session limit.</param>
    Task<TunnelSessionVM> OpenAsync(string hostId, string clientAddress, CancellationToken cancellationToken = default);

    /// <summary>Opens a session to the host whose pairing slot has this lookup key; the slot is used up.</summary>
    /// <param name="lookupKey">Derived from the invite code (<c>RelayPairingCode.DeriveLookupKey</c>).</param>
    Task<TunnelPairingSessionVM> OpenPairingAsync(string lookupKey, string clientAddress, CancellationToken cancellationToken = default);
}
