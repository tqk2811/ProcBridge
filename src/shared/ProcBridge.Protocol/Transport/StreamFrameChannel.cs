using System.Buffers;
using System.Buffers.Binary;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Protocol.Transport;

/// <summary>
/// Length-prefixed framing over a byte stream (see <see cref="Frame"/> for the layout). Sends are serialized by a
/// lock so several requests can share one connection; receiving is single reader.
/// Owns the stream only when <c>ownsStream</c> is true.
/// </summary>
public sealed class StreamFrameChannel : IFrameChannel
{
    private const int LengthPrefixSize = 4;

    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _maxFrameSize;
    private readonly IFrameProtector? _protector;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _lengthBuffer = new byte[LengthPrefixSize];
    private readonly byte[] _lengthBufferForSend = new byte[LengthPrefixSize];

    /// <param name="protector">Encryption layer; null sends plain frames (M2, before M5).</param>
    public StreamFrameChannel(Stream stream, bool ownsStream, int maxFrameSize, IFrameProtector? protector = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameSize, Frame.FixedBodySize);
        _stream = stream;
        _ownsStream = ownsStream;
        _maxFrameSize = maxFrameSize;
        _protector = protector;
    }

    public async ValueTask SendAsync(Frame frame, CancellationToken cancellationToken)
    {
        int bodyLength = frame.BodyLength;
        if (bodyLength > _maxFrameSize)
            throw new ProtocolStatusException(NtStatus.FrameTooLarge, $"Frame body {bodyLength} exceeds limit {_maxFrameSize}.");

        // Fixed part + header are copied into one buffer; the payload (file data) is written as is, without copy,
        // unless a protector has to transform the whole body.
        int prefixLength = LengthPrefixSize + Frame.FixedBodySize + frame.Header.Length;
        byte[] prefix = ArrayPool<byte>.Shared.Rent(_protector is null ? prefixLength : LengthPrefixSize + bodyLength);
        try
        {
            WriteFixedPart(prefix, frame, bodyLength);

            // Only the lock wait honours cancellation: abandoning a half written frame would corrupt the stream.
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_protector is null)
                {
                    await _stream.WriteAsync(prefix.AsMemory(0, prefixLength), CancellationToken.None).ConfigureAwait(false);
                    if (!frame.Payload.IsEmpty)
                        await _stream.WriteAsync(frame.Payload, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    frame.Payload.Span.CopyTo(prefix.AsSpan(prefixLength));
                    ReadOnlyMemory<byte> sealedBody = _protector.Protect(prefix.AsMemory(LengthPrefixSize, bodyLength));
                    BinaryPrimitives.WriteInt32LittleEndian(_lengthBufferForSend, sealedBody.Length);
                    await _stream.WriteAsync(_lengthBufferForSend, CancellationToken.None).ConfigureAwait(false);
                    await _stream.WriteAsync(sealedBody, CancellationToken.None).ConfigureAwait(false);
                }
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(prefix);
        }
    }

    /// <summary>Writes <c>u32 bodyLength | fixed part | header</c> at the start of <paramref name="buffer"/>.</summary>
    private static void WriteFixedPart(Span<byte> buffer, in Frame frame, int bodyLength)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buffer, bodyLength);
        buffer[4] = (byte)frame.Kind;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[5..], (ushort)frame.MessageType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[7..], frame.RequestId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[11..], frame.Header.Length);
        frame.Header.Span.CopyTo(buffer[(LengthPrefixSize + Frame.FixedBodySize)..]);
    }

    public async ValueTask<Frame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < LengthPrefixSize)
        {
            int n = await _stream.ReadAsync(_lengthBuffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                    return null;
                throw new ProtocolStatusException(NtStatus.NetworkUnreachable, "Connection closed inside a frame.", isConnectionFailure: true);
            }
            read += n;
        }

        int wireLength = BinaryPrimitives.ReadInt32LittleEndian(_lengthBuffer);
        int limit = _maxFrameSize + (_protector?.Overhead ?? 0);
        if (wireLength < Frame.FixedBodySize || wireLength > limit)
            throw new ProtocolStatusException(NtStatus.FrameTooLarge, $"Invalid frame length {wireLength} (limit {limit}).", isConnectionFailure: true);

        byte[] wire = new byte[wireLength];
        try
        {
            await _stream.ReadExactlyAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException e)
        {
            throw new ProtocolStatusException(NtStatus.NetworkUnreachable, "Connection closed inside a frame.", isConnectionFailure: true, inner: e);
        }

        ReadOnlyMemory<byte> body = _protector is null ? wire : _protector.Unprotect(wire);
        return Parse(body);
    }

    private static Frame Parse(ReadOnlyMemory<byte> body)
    {
        ReadOnlySpan<byte> span = body.Span;
        if (span.Length < Frame.FixedBodySize)
            throw new ProtocolStatusException(NtStatus.ProtocolError, "Frame body too short.", isConnectionFailure: true);

        var kind = (FrameKind)span[0];
        if (kind is not (FrameKind.Request or FrameKind.Response or FrameKind.Error))
            throw new ProtocolStatusException(NtStatus.ProtocolError, $"Unknown frame kind {span[0]}.", isConnectionFailure: true);

        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(span[7..]);
        if (headerLength < 0 || headerLength > span.Length - Frame.FixedBodySize)
            throw new ProtocolStatusException(NtStatus.ProtocolError, $"Invalid header length {headerLength}.", isConnectionFailure: true);

        return new Frame
        {
            Kind = kind,
            MessageType = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(span[1..]),
            RequestId = BinaryPrimitives.ReadUInt32LittleEndian(span[3..]),
            Header = body.Slice(Frame.FixedBodySize, headerLength),
            Payload = body[(Frame.FixedBodySize + headerLength)..],
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsStream)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
