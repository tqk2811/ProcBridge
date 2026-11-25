namespace ProcBridge.Relay.ViewModels.Tunnel;

/// <summary>
/// Control message (JSON text frame) sent BY the host on <c>/tunnel/control</c>: open its pairing slot under
/// <see cref="LookupKey"/> (derived from the invite code, <c>RelayPairingCode.DeriveLookupKey</c>), or close it when
/// <see cref="Type"/> is <c>pairingSlotCancel</c> (no key then).
/// </summary>
public sealed record TunnelPairingSlotMessageVM(string Type, string? LookupKey);
