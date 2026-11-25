using ProcBridge.Crypto.Noise;

namespace ProcBridge.Crypto.Identity;

/// <summary>
/// Long-term X25519 identity key of one machine (Broker on A, Agent on B). It is the Noise
/// static key. The instance owns the private key; dispose it when done.
/// </summary>
public sealed class IdentityKey : IDisposable
{
    public const int PublicKeySize = X25519KeyPair.KeySize;

    private IdentityKey(X25519KeyPair keyPair)
    {
        KeyPair = keyPair;
        Fingerprint = KeyFingerprint.Compute(keyPair.PublicKey);
    }

    /// <summary>Raw 32-byte public key, safe to share (pairing, relay registration).</summary>
    public ReadOnlySpan<byte> PublicKey => KeyPair.PublicKey;

    /// <summary>Human-readable fingerprint of <see cref="PublicKey"/>, see <see cref="KeyFingerprint"/>.</summary>
    public string Fingerprint { get; }

    internal X25519KeyPair KeyPair { get; }

    public static IdentityKey Generate() => new(X25519KeyPair.Generate());

    public static IdentityKey FromPrivateKey(ReadOnlySpan<byte> privateKey) => new(X25519KeyPair.FromPrivateKey(privateKey));

    /// <summary>Raw private key for persistence. Callers must protect and wipe the returned array.</summary>
    public byte[] ExportPrivateKey() => KeyPair.ExportPrivateKey();

    public void Dispose() => KeyPair.Dispose();
}
