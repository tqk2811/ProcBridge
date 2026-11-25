namespace ProcBridge.Transport.Secure.Enums;

/// <summary>One clear byte the server answers to <see cref="SecureConnectionOp.Join"/>.</summary>
public enum JoinResult : byte
{
    Accepted = 0,
    /// <summary>The server does not know the session (restarted, expired) or no longer trusts its peer: handshake again.</summary>
    UnknownSession = 1,
}
