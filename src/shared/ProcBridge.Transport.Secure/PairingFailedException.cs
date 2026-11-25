namespace ProcBridge.Transport.Secure;

/// <summary>Pairing did not complete: wrong code, rejected on machine B, timeout or a protocol failure.</summary>
public sealed class PairingFailedException : Exception
{
    public PairingFailedException(string message) : base(message)
    {
    }

    public PairingFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
