using System.IO.Pipes;
using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Interfaces;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol.Enums;

namespace ProcBridge.Tests.Lan;

/// <summary>Plays Hook.dll against a real BrokerHost: named pipe → Broker → TCP → Agent.</summary>
[TestClass]
public sealed class HookPipeEndToEndTests
{
    private const string VirtualRoot = @"V:\Remote\Proj";

    private delegate T BodyReader<T>(ref HookIpcReader reader);

    /// <summary>Minimal stand-in for the C++ pipe client.</summary>
    private sealed class FakeHook(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly byte[] _buffer = new byte[HookIpcProtocol.MaxMessageSize];
        private uint _nextId;

        public static async Task<FakeHook> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            pipe.ReadMode = PipeTransmissionMode.Message;
            return new FakeHook(pipe);
        }

        public async Task<(uint Status, byte[] Reply)> CallAsync(IHookIpcMessage request)
        {
            uint id = ++_nextId;
            int size = HookIpcCodec.Encode(_buffer, id, request);
            await pipe.WriteAsync(_buffer.AsMemory(0, size));
            int length = 0;
            do
            {
                length += await pipe.ReadAsync(_buffer.AsMemory(length));
            }
            while (!pipe.IsMessageComplete);
            byte[] reply = _buffer[..length];
            HookIpcHeader header = HookIpcCodec.ReadHeader(reply);
            Assert.AreEqual(id, header.RequestId);
            Assert.AreEqual(request.MessageType, header.MessageType);
            return (header.Status, reply);
        }

        public async Task<T> CallOkAsync<T>(IHookIpcMessage request, BodyReader<T> read)
        {
            (uint status, byte[] reply) = await CallAsync(request);
            Assert.AreEqual(0u, status, $"status 0x{status:X8}");
            var reader = new HookIpcReader(reply);
            return read(ref reader);
        }

        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }

    [TestMethod]
    public async Task HookRequests_AreServedThroughBroker()
    {
        using var share = new TempFolder("pipe");
        share.WriteFile(@"proj\src\a.cs", "class A {}"u8.ToArray());
        share.WriteFile(@"proj\readme.md", "# hi"u8.ToArray());
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();

        string session = Guid.NewGuid().ToString("N");
        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = session,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "proj")],
            CacheRoot = cacheDir.Path,
        });

        await using FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));

        HookHelloReply hello = await hook.CallOkAsync(new HookHelloRequest { ProcessId = 1, Architecture = HookArchitecture.X64 }, HookHelloReply.Read);
        CollectionAssert.AreEqual(new[] { VirtualRoot }, hello.VirtualRoots.ToArray());

        HookListReply list = await hook.CallOkAsync(new HookListRequest { MappingIndex = 0, RelativePath = "" }, HookListReply.Read);
        CollectionAssert.AreEqual(new[] { "readme.md", "src" }, list.Entries.Select(x => x.Name).ToArray());
        Assert.IsFalse(list.HasMore);

        HookStatReply stat = await hook.CallOkAsync(new HookStatRequest { MappingIndex = 0, RelativePath = @"src\a.cs" }, HookStatReply.Read);
        Assert.AreEqual(10L, stat.Info.Size);
        Assert.AreEqual(4096L, stat.Info.AllocationSize);

        HookResolveReply resolve = await hook.CallOkAsync(new HookResolveRequest
        {
            MappingIndex = 0, RelativePath = @"src\a.cs", DesiredAccess = 0x80100080 /* GENERIC_READ|SYNCHRONIZE|FILE_READ_ATTRIBUTES */, CreateDisposition = 1,
        }, HookResolveReply.Read);
        Assert.AreEqual("class A {}", File.ReadAllText(resolve.CachePath));
        StringAssert.StartsWith(resolve.CachePath, cacheDir.Path);
        Assert.AreEqual(stat.Info.LastWriteTime, File.GetLastWriteTimeUtc(resolve.CachePath).ToFileTimeUtc());

        HookResolveReply directory = await hook.CallOkAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = "src", CreateDisposition = 1 }, HookResolveReply.Read);
        Assert.IsTrue(Directory.Exists(directory.CachePath));
        Assert.AreNotEqual(0u, directory.Info.Attributes & 0x10);

        Assert.AreEqual((uint)NtStatus.ObjectNameNotFound, (await hook.CallAsync(new HookStatRequest { MappingIndex = 0, RelativePath = "missing.txt" })).Status);
        Assert.AreEqual((uint)NtStatus.ObjectNameInvalid, (await hook.CallAsync(new HookStatRequest { MappingIndex = 0, RelativePath = @"..\..\secret" })).Status);
        Assert.AreEqual((uint)NtStatus.InvalidParameter, (await hook.CallAsync(new HookStatRequest { MappingIndex = 5, RelativePath = "" })).Status);
        Assert.AreEqual((uint)NtStatus.MediaWriteProtected, (await hook.CallAsync(new HookResolveRequest
        {
            MappingIndex = 0, RelativePath = "readme.md", DesiredAccess = 0x40000000 /* GENERIC_WRITE */, CreateDisposition = 1,
        })).Status);
        Assert.AreEqual((uint)NtStatus.ObjectNameNotFound, (await hook.CallAsync(new HookResolveRequest
        {
            MappingIndex = 0, RelativePath = "new.txt", DesiredAccess = 0x40000000, CreateDisposition = 2 /* FILE_CREATE */,
        })).Status);
    }

    [TestMethod]
    public async Task SeveralHookThreads_UseSeparatePipeInstancesInParallel()
    {
        using var share = new TempFolder("pipe");
        for (int i = 0; i < 8; i++)
            share.WriteFile($"f{i}.txt", [(byte)('0' + i)]);
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = Guid.NewGuid().ToString("N");
        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = session, AgentHost = "127.0.0.1", AgentPort = agent.Port, ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "")], CacheRoot = cacheDir.Path,
        });

        string[] contents = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
            HookResolveReply reply = await hook.CallOkAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = $"f{i}.txt", CreateDisposition = 1 }, HookResolveReply.Read);
            return File.ReadAllText(reply.CachePath);
        }));
        CollectionAssert.AreEqual(Enumerable.Range(0, 8).Select(i => i.ToString()).ToArray(), contents);
    }
}
