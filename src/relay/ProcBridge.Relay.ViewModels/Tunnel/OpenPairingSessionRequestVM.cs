namespace ProcBridge.Relay.ViewModels.Tunnel;

public sealed record OpenPairingSessionRequestVM
{
    /// <summary>
    /// Lookup key derived from the pairing invite code machine B shows (<c>RelayPairingCode.DeriveLookupKey</c>); the
    /// relay never sees the code itself.
    /// </summary>
    public required string LookupKey { get; init; }
}
