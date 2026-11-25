using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;

namespace ProcBridge.Tests.HookIpc;

[TestClass]
public sealed class HookIpcCodecTests
{
    private static readonly HookFileInfo SampleInfo = new()
    {
        Attributes = 0x21,
        Size = 5_000_000_000,
        AllocationSize = 5_000_003_584,
        CreationTime = 1,
        LastAccessTime = 2,
        LastWriteTime = 3,
        ChangeTime = 4,
        FileId = ulong.MaxValue,
    };

    private delegate T BodyReader<T>(ref HookIpcReader reader);

    private static T RoundTrip<T>(IHookIpcMessage message, BodyReader<T> read, uint requestId = 77)
    {
        byte[] buffer = new byte[HookIpcProtocol.MaxMessageSize];
        int size = HookIpcCodec.Encode(buffer, requestId, message);
        ReadOnlySpan<byte> bytes = buffer.AsSpan(0, size);
        HookIpcHeader header = HookIpcCodec.ReadHeader(bytes);
        Assert.AreEqual((uint)size, header.Size);
        Assert.AreEqual(message.MessageType, header.MessageType);
        Assert.AreEqual(requestId, header.RequestId);
        Assert.AreEqual(0u, header.Status);
        var reader = new HookIpcReader(bytes);
        T result = read(ref reader);
        Assert.AreEqual(0, reader.Remaining, "Body has trailing bytes.");
        return result;
    }

    [TestMethod]
    public void Hello_RoundTrips()
    {
        HookHelloRequest request = RoundTrip(new HookHelloRequest { ProcessId = 4242, Architecture = HookArchitecture.X86 }, HookHelloRequest.Read);
        Assert.AreEqual(4242u, request.ProcessId);
        Assert.AreEqual(HookArchitecture.X86, request.Architecture);

        HookHelloReply reply = RoundTrip(new HookHelloReply { VirtualRoots = [@"D:\Remote\Proj", "V:", "Đường dẫn"] }, HookHelloReply.Read);
        CollectionAssert.AreEqual(new[] { @"D:\Remote\Proj", "V:", "Đường dẫn" }, reply.VirtualRoots.ToArray());
    }

    [TestMethod]
    public void StatAndResolve_RoundTrip()
    {
        HookStatRequest stat = RoundTrip(new HookStatRequest { MappingIndex = 3, RelativePath = @"src\a.cs" }, HookStatRequest.Read);
        Assert.AreEqual(3, stat.MappingIndex);
        Assert.AreEqual(@"src\a.cs", stat.RelativePath);
        Assert.AreEqual(SampleInfo, RoundTrip(new HookStatReply { Info = SampleInfo }, HookStatReply.Read).Info);

        HookResolveRequest resolve = RoundTrip(new HookResolveRequest
        {
            MappingIndex = 1, Flags = 2, DesiredAccess = 0x80100080, CreateDisposition = 1, CreateOptions = 0x60, RelativePath = "",
        }, HookResolveRequest.Read);
        Assert.AreEqual(0x80100080u, resolve.DesiredAccess);
        Assert.AreEqual(1u, resolve.CreateDisposition);
        Assert.AreEqual(0x60u, resolve.CreateOptions);
        Assert.AreEqual(2, resolve.Flags);
        Assert.AreEqual(string.Empty, resolve.RelativePath);

        HookResolveReply reply = RoundTrip(new HookResolveReply { Info = SampleInfo, CachePath = @"C:\cache\a.cs" }, HookResolveReply.Read);
        Assert.AreEqual(SampleInfo, reply.Info);
        Assert.AreEqual(@"C:\cache\a.cs", reply.CachePath);
    }

    [TestMethod]
    public void List_RoundTrips()
    {
        HookListRequest request = RoundTrip(new HookListRequest { MappingIndex = 0, StartIndex = 500, RelativePath = "dir" }, HookListRequest.Read);
        Assert.AreEqual(500u, request.StartIndex);

        HookListReply reply = RoundTrip(new HookListReply { Entries = [new(SampleInfo, "a"), new(SampleInfo with { Attributes = 0x10 }, "sub")], HasMore = true }, HookListReply.Read);
        Assert.IsTrue(reply.HasMore);
        Assert.HasCount(2, reply.Entries);
        Assert.AreEqual("sub", reply.Entries[1].Name);
        Assert.AreEqual(0x10u, reply.Entries[1].Info.Attributes);
    }

    [TestMethod]
    public void ErrorReply_HasHeaderOnly()
    {
        byte[] buffer = new byte[64];
        int size = HookIpcCodec.EncodeError(buffer, HookIpcMessageType.Resolve, 9, 0xC0000034);
        Assert.AreEqual(HookIpcProtocol.HeaderSize, size);
        HookIpcHeader header = HookIpcCodec.ReadHeader(buffer.AsSpan(0, size));
        Assert.AreEqual(0xC0000034u, header.Status);
        Assert.AreEqual(9u, header.RequestId);
    }

    [TestMethod]
    public void LayoutIsFixed()
    {
        byte[] buffer = new byte[256];
        int size = HookIpcCodec.Encode(buffer, 0x01020304, new HookStatRequest { MappingIndex = 0x0A0B, RelativePath = "ab" });
        // header(16) + u16 + u16 + u16 len + 2 chars
        Assert.AreEqual(16 + 2 + 2 + 2 + 4, size);
        CollectionAssert.AreEqual(
            new byte[] { 26, 0, 0, 0, 1, 0, 2, 0, 4, 3, 2, 1, 0, 0, 0, 0, 0x0B, 0x0A, 0, 0, 2, 0, (byte)'a', 0, (byte)'b', 0 },
            buffer[..size]);

        size = HookIpcCodec.Encode(buffer, 0, new HookStatReply { Info = SampleInfo });
        Assert.AreEqual(HookIpcProtocol.HeaderSize + HookIpcProtocol.FileInfoSize, size);
    }

    [TestMethod]
    public void MalformedMessages_Throw()
    {
        byte[] buffer = new byte[256];
        int size = HookIpcCodec.Encode(buffer, 1, new HookStatRequest { MappingIndex = 0, RelativePath = "abc" });

        Assert.ThrowsExactly<HookIpcFormatException>(() => HookIpcCodec.ReadHeader(buffer.AsSpan(0, size - 1)));
        Assert.ThrowsExactly<HookIpcFormatException>(() => HookIpcCodec.ReadHeader(buffer.AsSpan(0, 8)));

        // Header claims the right size, but the string length points past the end.
        buffer[HookIpcProtocol.HeaderSize + 4] = 200;
        byte[] message = buffer[..size];
        Assert.ThrowsExactly<HookIpcFormatException>(() =>
        {
            var reader = new HookIpcReader(message);
            HookStatRequest.Read(ref reader);
        });

        Assert.ThrowsExactly<HookIpcFormatException>(() =>
            HookIpcCodec.Encode(new byte[64], 1, new HookStatRequest { MappingIndex = 0, RelativePath = new string('x', 100) }));
    }
}
