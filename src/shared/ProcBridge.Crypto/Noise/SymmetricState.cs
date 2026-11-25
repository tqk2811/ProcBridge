using System.Security.Cryptography;
using System.Text;

namespace ProcBridge.Crypto.Noise;

/// <summary>Noise SymmetricState with SHA-256 (Noise spec section 5.2).</summary>
internal sealed class SymmetricState : IDisposable
{
    public const int HashSize = 32;

    private readonly CipherState _cipher = new();
    private byte[] _chainingKey;
    private byte[] _hash;

    public SymmetricState(string protocolName)
    {
        var name = Encoding.ASCII.GetBytes(protocolName);
        if (name.Length <= HashSize)
        {
            _hash = new byte[HashSize];
            name.CopyTo(_hash, 0);
        }
        else
        {
            _hash = SHA256.HashData(name);
        }
        _chainingKey = (byte[])_hash.Clone();
    }

    public byte[] Hash => (byte[])_hash.Clone();

    public bool HasKey => _cipher.HasKey;

    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, tempKey) = Hkdf(_chainingKey, inputKeyMaterial);
        CryptographicOperations.ZeroMemory(_chainingKey);
        _chainingKey = ck;
        _cipher.InitializeKey(tempKey);
        CryptographicOperations.ZeroMemory(tempKey);
    }

    public void MixHash(ReadOnlySpan<byte> data)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_hash);
        sha.AppendData(data);
        _hash = sha.GetHashAndReset();
    }

    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = _cipher.EncryptWithAd(_hash, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = _cipher.DecryptWithAd(_hash, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>Returns the two transport keys (initiator-to-responder, responder-to-initiator).</summary>
    public (byte[] InitiatorToResponder, byte[] ResponderToInitiator) Split()
        => Hkdf(_chainingKey, ReadOnlySpan<byte>.Empty);

    /// <summary>Noise HKDF with two outputs, built on HMAC-SHA256.</summary>
    private static (byte[], byte[]) Hkdf(ReadOnlySpan<byte> chainingKey, ReadOnlySpan<byte> inputKeyMaterial)
    {
        var tempKey = HMACSHA256.HashData(chainingKey, inputKeyMaterial);
        var output1 = HMACSHA256.HashData(tempKey, (ReadOnlySpan<byte>)[0x01]);
        var buffer = new byte[HashSize + 1];
        output1.CopyTo(buffer, 0);
        buffer[HashSize] = 0x02;
        var output2 = HMACSHA256.HashData(tempKey, buffer);
        CryptographicOperations.ZeroMemory(tempKey);
        CryptographicOperations.ZeroMemory(buffer);
        return (output1, output2);
    }

    public void Dispose()
    {
        _cipher.Dispose();
        CryptographicOperations.ZeroMemory(_chainingKey);
    }
}
