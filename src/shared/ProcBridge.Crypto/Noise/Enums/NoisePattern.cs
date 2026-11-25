namespace ProcBridge.Crypto.Noise.Enums;

/// <summary>Supported Noise handshake patterns (all with 25519, ChaChaPoly, SHA256).</summary>
public enum NoisePattern
{
    /// <summary>Mutual static exchange without prior knowledge. Used for first-time pairing.</summary>
    XX,

    /// <summary>Initiator already knows the responder static key. Used for sessions after pairing.</summary>
    IK,
}
