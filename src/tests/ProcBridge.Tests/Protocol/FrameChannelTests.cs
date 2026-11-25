using System.Buffers.Binary;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class FrameChannelTests
{
    private const int MaxFrame = 64 * 1024;

    private static Frame MakeFrame(uint id, int payloadLength) => new()
    {
        Kind = FrameKind.Response,
        MessageType = MessageType.FsRead,
        RequestId = id,
        Header = ProtocolSerializer.Serialize(new FsReadResponse { Version = new FsVersion { LastWriteTime = id, Size = payloadLength } }),
        Payload = Enumerable.Range(0, payloadLength).Select(i => (byte)(i * 7 + id)).ToArray(),
    };

    private static async Task<List<Frame>> RoundTripAsync(IEnumerable<Frame> frames, IFrameProtector? protector)
    {
        var stream = new MemoryStream();
        await using (var writer = new StreamFrameChannel(stream, ownsStream: false, MaxFrame, protector))
        {
            foreach (Frame frame in frames)
                await writer.SendAsync(frame, CancellationToken.None);
        }

        stream.Position = 0;
        var result = new List<Frame>();
        await using var reader = new StreamFrameChannel(stream, ownsStream: false, MaxFrame, protector);
        while (await reader.ReceiveAsync(CancellationToken.None) is { } frame)
            result.Add(frame);
        return result;
    }

    private static void AssertFrameEqual(Frame expected, Frame actual)
    {
        Assert.AreEqual(expected.Kind, actual.Kind);
        Assert.AreEqual(expected.MessageType, actual.MessageType);
        Assert.AreEqual(expected.RequestId, actual.RequestId);
        CollectionAssert.AreEqual(expected.Header.ToArray(), actual.Header.ToArray());
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
    }

    [TestMethod]
    public async Task Frames_RoundTripInOrder()
    {
        Frame[] frames = [MakeFrame(1, 0), MakeFrame(2, 10), MakeFrame(3, 40000)];
        List<Frame> received = await RoundTripAsync(frames, protector: null);
        Assert.HasCount(3, received);
        for (int i = 0; i < frames.Length; i++)
            AssertFrameEqual(frames[i], received[i]);
    }

    [TestMethod]
    public async Task Frames_RoundTripThroughProtector()
    {
        Frame[] frames = [MakeFrame(5, 1000), MakeFrame(6, 0)];
        List<Frame> received = await RoundTripAsync(frames, new XorProtector());
        Assert.HasCount(2, received);
        AssertFrameEqual(frames[0], received[0]);
        AssertFrameEqual(frames[1], received[1]);
    }

    [TestMethod]
    public async Task Protector_RejectsTamperedFrame()
    {
        var stream = new MemoryStream();
        await using (var writer = new StreamFrameChannel(stream, ownsStream: false, MaxFrame, new XorProtector()))
            await writer.SendAsync(MakeFrame(1, 100), CancellationToken.None);
        stream.GetBuffer()[20] ^= 0xFF;
        stream.Position = 0;
        await using var reader = new StreamFrameChannel(stream, ownsStream: false, MaxFrame, new XorProtector());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await reader.ReceiveAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Send_RejectsFrameAboveLimit()
    {
        await using var channel = new StreamFrameChannel(new MemoryStream(), ownsStream: true, MaxFrame);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(async () => await channel.SendAsync(MakeFrame(1, MaxFrame), CancellationToken.None));
        Assert.AreEqual(NtStatus.FrameTooLarge, e.Status);
    }

    [TestMethod]
    public async Task Receive_RejectsLengthAboveLimit()
    {
        byte[] wire = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(wire, MaxFrame + 1);
        await using var channel = new StreamFrameChannel(new MemoryStream(wire), ownsStream: true, MaxFrame);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(async () => await channel.ReceiveAsync(CancellationToken.None));
        Assert.AreEqual(NtStatus.FrameTooLarge, e.Status);
        Assert.IsTrue(e.IsConnectionFailure);
    }

    [TestMethod]
    public async Task Receive_TruncatedFrameIsConnectionFailure()
    {
        var stream = new MemoryStream();
        await using (var writer = new StreamFrameChannel(stream, ownsStream: false, MaxFrame))
            await writer.SendAsync(MakeFrame(1, 100), CancellationToken.None);
        byte[] truncated = stream.ToArray()[..50];
        await using var reader = new StreamFrameChannel(new MemoryStream(truncated), ownsStream: true, MaxFrame);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(async () => await reader.ReceiveAsync(CancellationToken.None));
        Assert.IsTrue(e.IsConnectionFailure);
    }

    [TestMethod]
    public async Task Receive_BadHeaderLengthIsProtocolError()
    {
        byte[] wire = new byte[4 + Frame.FixedBodySize];
        BinaryPrimitives.WriteInt32LittleEndian(wire, Frame.FixedBodySize);
        wire[4] = (byte)FrameKind.Request;
        BinaryPrimitives.WriteInt32LittleEndian(wire.AsSpan(4 + 7), 1000);
        await using var reader = new StreamFrameChannel(new MemoryStream(wire), ownsStream: true, MaxFrame);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(async () => await reader.ReceiveAsync(CancellationToken.None));
        Assert.AreEqual(NtStatus.ProtocolError, e.Status);
    }

    /// <summary>Stand-in for the M5 AEAD layer: XOR "encryption" plus a 1-byte checksum tag.</summary>
    private sealed class XorProtector : IFrameProtector
    {
        public int Overhead => 1;

        public ReadOnlyMemory<byte> Protect(ReadOnlyMemory<byte> body)
        {
            byte[] result = new byte[body.Length + 1];
            byte sum = 0;
            for (int i = 0; i < body.Length; i++)
            {
                sum += body.Span[i];
                result[i] = (byte)(body.Span[i] ^ 0x5A);
            }
            result[^1] = sum;
            return result;
        }

        public ReadOnlyMemory<byte> Unprotect(ReadOnlyMemory<byte> data)
        {
            byte[] result = new byte[data.Length - 1];
            byte sum = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(data.Span[i] ^ 0x5A);
                sum += result[i];
            }
            if (sum != data.Span[^1])
                throw new InvalidDataException("Frame authentication failed.");
            return result;
        }
    }
}
