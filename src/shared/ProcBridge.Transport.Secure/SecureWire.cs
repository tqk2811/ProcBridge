using System.Buffers.Binary;
using System.Text;
using ProcBridge.Crypto;
using ProcBridge.Crypto.Sessions;
using ProcBridge.Protocol;
using ProcBridge.Transport.Secure.Enums;

namespace ProcBridge.Transport.Secure;

/// <summary>
/// Clear-text preamble of a secure connection, before any frame:
/// <list type="bullet">
/// <item><c>Handshake</c>: A sends <c>magic | op | u16 len | IK msg1</c>, B answers <c>u16 len | IK msg2</c>, A sends
/// <c>u32 channel id</c>; then protected frames.</item>
/// <item><c>Join</c>: A sends <c>magic | op | session id (16) | u32 channel id</c>, B answers one <see cref="JoinResult"/>
/// byte; then protected frames.</item>
/// <item><c>Pairing</c>: A sends <c>magic | op</c>, then the three Noise XX messages (<c>u16 len | msg</c> each way),
/// then pairing messages on the pairing channel, each <c>u16 len | sealed</c>.</item>
/// </list>
/// The session id is the start of the Noise handshake hash, which both ends know and nobody can choose. The prologue
/// binds the handshake to <see cref="ProtocolInfo.Version"/>, so two builds speaking different protocols fail the
/// handshake instead of misreading frames.
/// </summary>
internal static class SecureWire
{
    public const int SessionIdSize = 16;

    /// <summary>Longest machine name exchanged during pairing, in UTF-8 bytes.</summary>
    public const int MaxNameBytes = 256;

    public static ReadOnlySpan<byte> Magic => "PBS1"u8;

    public static byte[] Prologue { get; } = Encoding.ASCII.GetBytes($"ProcBridge/{ProtocolInfo.Version}");

    public static string SessionKey(ReadOnlySpan<byte> handshakeHash) => Convert.ToHexString(handshakeHash[..SessionIdSize]);

    public static async ValueTask WritePreambleAsync(Stream stream, SecureConnectionOp op, CancellationToken cancellationToken)
    {
        byte[] preamble = new byte[Magic.Length + 1];
        Magic.CopyTo(preamble);
        preamble[^1] = (byte)op;
        await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the magic and the op; null when the peer is not a secure ProcBridge client.</summary>
    public static async ValueTask<SecureConnectionOp?> ReadPreambleAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] preamble = new byte[Magic.Length + 1];
        await stream.ReadExactlyAsync(preamble, cancellationToken).ConfigureAwait(false);
        if (!preamble.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            return null;
        var op = (SecureConnectionOp)preamble[^1];
        return op is SecureConnectionOp.Handshake or SecureConnectionOp.Join or SecureConnectionOp.Pairing ? op : null;
    }

    /// <summary>Writes <c>u16 length | message</c> and flushes.</summary>
    public static async ValueTask WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        if (message.Length > ushort.MaxValue)
            throw new ArgumentException("Handshake message too long.", nameof(message));
        byte[] buffer = new byte[sizeof(ushort) + message.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, (ushort)message.Length);
        message.CopyTo(buffer.AsMemory(sizeof(ushort)));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <exception cref="EndOfStreamException">The peer closed the connection.</exception>
    public static async ValueTask<byte[]> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[sizeof(ushort)];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        byte[] message = new byte[BinaryPrimitives.ReadUInt16LittleEndian(length)];
        await stream.ReadExactlyAsync(message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    /// <summary>Seals <paramref name="message"/> on the pairing channel and writes it as <c>u16 length | sealed</c>.</summary>
    public static ValueTask WriteSealedAsync(Stream stream, SecureChannel channel, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
        => channel.SealAndWriteAsync(message, default, (frame, token) => WriteMessageAsync(stream, frame, token), cancellationToken);

    /// <exception cref="CryptoProtocolException">The message is forged, corrupted or out of order.</exception>
    public static async ValueTask<byte[]> ReadSealedAsync(Stream stream, SecureChannel channel, CancellationToken cancellationToken)
        => channel.Open(await ReadMessageAsync(stream, cancellationToken).ConfigureAwait(false));

    public static async ValueTask WriteUInt32Async(Stream stream, uint value, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
