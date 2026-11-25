using System.Buffers.Binary;
using System.Security.Cryptography;
using ProcBridge.Crypto.Pairing.Enums;
using ProcBridge.Crypto.Sessions;

namespace ProcBridge.Crypto.Pairing;

/// <summary>Shared state and formulas of the pairing exchange, bound to one session's handshake hash.</summary>
internal sealed class PairingTranscript
{
    public const int NonceSize = 32;
    public const int CodeDigits = 6;
    private const int MessageSize = 1 + NonceSize;

    private static readonly byte[] _commitLabel = "ProcBridge pairing commit v1"u8.ToArray();
    private static readonly byte[] _codeLabel = "ProcBridge pairing code v1"u8.ToArray();
    private static readonly byte[] _confirmLabel = "ProcBridge pairing confirm v1"u8.ToArray();

    private readonly byte[] _handshakeHash;

    public PairingTranscript(SecureSession session)
    {
        _handshakeHash = session.HandshakeHash.ToArray();
    }

    public byte[] Commitment(ReadOnlySpan<byte> initiatorNonce) => Hash(_commitLabel, initiatorNonce, default);

    /// <summary>Short code: 6 decimal digits from SHA-256(label, h, Na, Nb).</summary>
    public string Code(ReadOnlySpan<byte> initiatorNonce, ReadOnlySpan<byte> responderNonce)
    {
        var digest = Hash(_codeLabel, initiatorNonce, responderNonce);
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000u;
        return value.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public byte[] Confirmation(ReadOnlySpan<byte> initiatorNonce, ReadOnlySpan<byte> responderNonce)
        => Hash(_confirmLabel, initiatorNonce, responderNonce);

    private byte[] Hash(byte[] label, ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(label);
        sha.AppendData(_handshakeHash);
        sha.AppendData(first);
        sha.AppendData(second);
        return sha.GetHashAndReset();
    }

    public static byte[] Pack(PairingMessageType type, ReadOnlySpan<byte> body)
    {
        var message = new byte[MessageSize];
        message[0] = (byte)type;
        body.CopyTo(message.AsSpan(1));
        return message;
    }

    public static byte[] Unpack(PairingMessageType expected, ReadOnlySpan<byte> message)
    {
        if (message.Length != MessageSize || message[0] != (byte)expected)
            throw new CryptoProtocolException($"Expected pairing message {expected}.");
        return message[1..].ToArray();
    }

    /// <summary>Keeps digits only, so "123 456" and "123-456" are accepted.</summary>
    public static string NormalizeCode(string typed) => new(typed.Where(char.IsAsciiDigit).ToArray());
}
