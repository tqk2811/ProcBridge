namespace ProcBridge.Crypto;

/// <summary>
/// Thrown when a peer violates the secure protocol: failed handshake, untrusted key,
/// tampered or replayed frame, wrong pairing code. The connection must be dropped.
/// </summary>
public sealed class CryptoProtocolException : Exception
{
    public CryptoProtocolException(string message) : base(message)
    {
    }

    public CryptoProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
