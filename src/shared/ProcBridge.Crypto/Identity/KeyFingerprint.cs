using System.Security.Cryptography;
using System.Text;

namespace ProcBridge.Crypto.Identity;

/// <summary>
/// Fingerprint of a public key for users to compare out of band (anti key-swap by the Relay).
/// Format: SHA-256 over a domain label and the key, first 20 bytes, base32 (RFC 4648 alphabet),
/// in 8 groups of 4 characters, e.g. <c>ABCD-EFGH-IJKL-MNOP-QRST-UVWX-YZ23-4567</c>.
/// </summary>
public static class KeyFingerprint
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int FingerprintBytes = 20;
    private static readonly byte[] _label = Encoding.ASCII.GetBytes("ProcBridge identity fingerprint v1");

    public static string Compute(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != IdentityKey.PublicKeySize)
            throw new ArgumentException($"Public key must be {IdentityKey.PublicKeySize} bytes.", nameof(publicKey));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(_label);
        sha.AppendData(publicKey);
        var digest = sha.GetHashAndReset();

        var builder = new StringBuilder(39);
        int buffer = 0, bits = 0, written = 0;
        for (int i = 0; i < FingerprintBytes; i++)
        {
            buffer = (buffer << 8) | digest[i];
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                if (written > 0 && written % 4 == 0)
                    builder.Append('-');
                builder.Append(Alphabet[(buffer >> bits) & 31]);
                written++;
            }
        }
        return builder.ToString();
    }
}
