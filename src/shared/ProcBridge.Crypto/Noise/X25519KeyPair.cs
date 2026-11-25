using NSec.Cryptography;

namespace ProcBridge.Crypto.Noise;

/// <summary>
/// X25519 key pair backed by NSec (libsodium). Owns the private key and zeroes it on dispose.
/// </summary>
internal sealed class X25519KeyPair : IDisposable
{
    public const int KeySize = 32;

    private static readonly KeyAgreementAlgorithm _algorithm = KeyAgreementAlgorithm.X25519;

    private readonly Key _key;

    private X25519KeyPair(Key key)
    {
        _key = key;
        PublicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
    }

    public byte[] PublicKey { get; }

    public static X25519KeyPair Generate()
    {
        var parameters = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        return new X25519KeyPair(new Key(_algorithm, in parameters));
    }

    public static X25519KeyPair FromPrivateKey(ReadOnlySpan<byte> privateKey)
    {
        if (privateKey.Length != KeySize)
            throw new ArgumentException($"X25519 private key must be {KeySize} bytes.", nameof(privateKey));
        var parameters = new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        return new X25519KeyPair(Key.Import(_algorithm, privateKey, KeyBlobFormat.RawPrivateKey, in parameters));
    }

    public byte[] ExportPrivateKey() => _key.Export(KeyBlobFormat.RawPrivateKey);

    /// <summary>Raw X25519 output. Rejects malformed or low-order remote keys.</summary>
    public byte[] Agree(ReadOnlySpan<byte> remotePublicKey)
    {
        if (remotePublicKey.Length != KeySize)
            throw new CryptoProtocolException("Remote X25519 public key has the wrong size.");
        if (!NSec.Cryptography.PublicKey.TryImport(_algorithm, remotePublicKey, KeyBlobFormat.RawPublicKey, out var remote) || remote is null)
            throw new CryptoProtocolException("Remote X25519 public key is invalid.");
        var parameters = new SharedSecretCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport };
        using var shared = _algorithm.Agree(_key, remote, in parameters)
            ?? throw new CryptoProtocolException("X25519 agreement produced an all-zero result.");
        return shared.Export(SharedSecretBlobFormat.RawSharedSecret);
    }

    public void Dispose() => _key.Dispose();
}
