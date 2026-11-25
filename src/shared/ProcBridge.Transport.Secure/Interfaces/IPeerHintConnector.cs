namespace ProcBridge.Transport.Secure.Interfaces;

/// <summary>
/// A connector that learned, while connecting, which machine it claims to reach (the relay answers a pairing lookup with
/// the HostId of the slot). <see cref="PairingClient"/> refuses to pair when the handshake proves another key: a relay
/// that routes elsewhere is caught even before the users compare fingerprints.
/// </summary>
public interface IPeerHintConnector : IStreamConnector
{
    /// <summary>Fingerprint the peer should prove, known after <see cref="IStreamConnector.ConnectAsync"/>; null when unknown.</summary>
    string? ExpectedPeerFingerprint { get; }
}
