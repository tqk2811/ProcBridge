namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// Control message (JSON text frame) pushed to the host about its pairing slot: <c>pairingSlotRegistered</c> (open until
/// <see cref="ExpiresAt"/>), <c>pairingSlotRejected</c> (pick a new code) or <c>pairingSlotClaimed</c> (a client used it;
/// <see cref="SessionId"/> is the pairing session). <see cref="LookupKey"/> says which slot, since the host may have
/// replaced it meanwhile. <see cref="ExpiresInSeconds"/> is the slot lifetime from now: the host times the expiry on its
/// own clock with it, since the two clocks may differ.
/// </summary>
public sealed record TunnelPairingSlotStateMessageVM(
    string Type, string LookupKey, DateTime? ExpiresAt = null, Guid? SessionId = null, double? ExpiresInSeconds = null);
