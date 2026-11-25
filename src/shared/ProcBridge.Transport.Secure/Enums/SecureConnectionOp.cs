namespace ProcBridge.Transport.Secure.Enums;

/// <summary>What a new connection asks for, sent in clear right after the magic.</summary>
public enum SecureConnectionOp : byte
{
    /// <summary>Noise IK handshake that starts a new session; the connection then becomes its first channel.</summary>
    Handshake = 1,
    /// <summary>Another channel of a session made earlier on another connection.</summary>
    Join = 2,
    /// <summary>First-time pairing: Noise XX, then the short code exchange on the pairing channel.</summary>
    Pairing = 3,
}
