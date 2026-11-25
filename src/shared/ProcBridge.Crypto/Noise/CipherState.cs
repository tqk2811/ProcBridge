using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ProcBridge.Crypto.Noise;

/// <summary>
/// Noise CipherState for ChaChaPoly: key k plus 64-bit nonce n (Noise spec section 5.1).
/// Not thread-safe; used only during the handshake.
/// </summary>
internal sealed class CipherState : IDisposable
{
    public const int KeySize = 32;
    public const int TagSize = 16;
    public const int NonceSize = 12;

    private byte[]? _key;
    private ChaCha20Poly1305? _aead;
    private ulong _nonce;

    public bool HasKey => _key is not null;

    public void InitializeKey(ReadOnlySpan<byte> key)
    {
        Dispose();
        _key = key.ToArray();
        _aead = new ChaCha20Poly1305(_key);
        _nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (_aead is null)
            return plaintext.ToArray();
        if (_nonce == ulong.MaxValue)
            throw new CryptoProtocolException("Noise nonce exhausted.");
        var output = new byte[plaintext.Length + TagSize];
        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteNonce(nonce, _nonce);
        _aead.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length), output.AsSpan(plaintext.Length), ad);
        _nonce++;
        return output;
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (_aead is null)
            return ciphertext.ToArray();
        if (_nonce == ulong.MaxValue)
            throw new CryptoProtocolException("Noise nonce exhausted.");
        if (ciphertext.Length < TagSize)
            throw new CryptoProtocolException("Noise ciphertext is too short.");
        var plain = new byte[ciphertext.Length - TagSize];
        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteNonce(nonce, _nonce);
        try
        {
            _aead.Decrypt(nonce, ciphertext[..plain.Length], ciphertext[plain.Length..], plain, ad);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new CryptoProtocolException("Noise message authentication failed.", ex);
        }
        _nonce++;
        return plain;
    }

    /// <summary>ChaChaPoly nonce: 32 zero bits followed by the little-endian 64-bit counter.</summary>
    public static void WriteNonce(Span<byte> destination, ulong counter)
    {
        destination[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], counter);
    }

    /// <summary>Noise REKEY(k): ENCRYPT(k, 2^64-1, empty, 32 zero bytes), first 32 bytes.</summary>
    public static byte[] Rekey(ReadOnlySpan<byte> key)
    {
        using var aead = new ChaCha20Poly1305(key);
        Span<byte> nonce = stackalloc byte[NonceSize];
        WriteNonce(nonce, ulong.MaxValue);
        Span<byte> zeros = stackalloc byte[KeySize];
        zeros.Clear();
        var newKey = new byte[KeySize];
        Span<byte> tag = stackalloc byte[TagSize];
        aead.Encrypt(nonce, zeros, newKey, tag);
        return newKey;
    }

    public void Dispose()
    {
        _aead?.Dispose();
        _aead = null;
        if (_key is not null)
            CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }
}
