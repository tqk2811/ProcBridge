namespace ProcBridge.Relay.ViewModels.Tunnel;

public sealed record OpenTunnelSessionRequestVM
{
    /// <summary>
    /// Fingerprint of the host's Noise identity key (<c>KeyFingerprint</c>), known to machine A from pairing. The relay
    /// grants nothing: the end-to-end Noise IK handshake inside the lanes is what authenticates both ends.
    /// </summary>
    public required string HostId { get; init; }
}
