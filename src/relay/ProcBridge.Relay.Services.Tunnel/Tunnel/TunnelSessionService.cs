using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel;

public sealed class TunnelSessionService : ITunnelSessionService
{
    /// <summary>Longer than any fingerprint (39 characters): rejects junk before it reaches the registry.</summary>
    private const int MaxHostIdLength = 64;

    private readonly ITunnelHub _hub;

    public TunnelSessionService(ITunnelHub hub) => _hub = hub;

    public Task<TunnelSessionVM> OpenAsync(string hostId, string clientAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(hostId) || hostId.Length > MaxHostIdLength)
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, "HostId không hợp lệ.");
        return _hub.OpenSessionAsync(hostId.Trim(), clientAddress, cancellationToken);
    }

    public Task<TunnelPairingSessionVM> OpenPairingAsync(string lookupKey, string clientAddress, CancellationToken cancellationToken = default)
    {
        if (!RelayPairingCode.IsLookupKey(lookupKey))
            throw ServiceException.BadRequest(RelayErrors.ValidationFailed, "Khoá tra cứu ghép cặp không hợp lệ.");
        return _hub.OpenPairingSessionAsync(lookupKey, clientAddress, cancellationToken);
    }
}
