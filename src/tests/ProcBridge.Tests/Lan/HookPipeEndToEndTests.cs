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
        // Machine B's own allocation size (a tiny file may be resident in the MFT, so it is not a whole cluster).
        Assert.IsGreaterThanOrEqualTo(10L, stat.Info.AllocationSize);
        Assert.AreNotEqual(0ul, stat.Info.FileId);

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
        // Creating a file on the share is a write, so it is refused as such even though the file does not exist.
        Assert.AreEqual((uint)NtStatus.MediaWriteProtected, (await hook.CallAsync(new HookResolveRequest
        {
            MappingIndex = 0, RelativePath = "new.txt", DesiredAccess = 0x40000000, CreateDisposition = 2 /* FILE_CREATE */,
        })).Status);
    }

    [TestMethod]
    public async Task WriteIntents_AreRefusedAndReadOnlyOpensWork()
    {
        using var share = new TempFolder("pipe");
        share.WriteFile("a.txt", "hello"u8.ToArray());
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = Guid.NewGuid().ToString("N");
        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = session, AgentHost = "127.0.0.1", AgentPort = agent.Port, ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "")], CacheRoot = cacheDir.Path,
        });
        await using FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));

        const uint MaximumAllowed = 0x02000000;
        const uint ReadAccess = 0x80100080;
        async Task<uint> ResolveAsync(string path, uint access, uint disposition)
            => (await hook.CallAsync(new HookResolveRequest { MappingIndex = 0, RelativePath = path, DesiredAccess = access, CreateDisposition = disposition })).Status;

        // MAXIMUM_ALLOWED asks for every right the object grants, write included.
        Assert.AreEqual((uint)NtStatus.MediaWriteProtected, await ResolveAsync("a.txt", MaximumAllowed, 1));
        // Dispositions that create a file are refused when it is missing; the creating ones also when it exists.
        foreach (uint disposition in new uint[] { 2, 3, 5 })
            Assert.AreEqual((uint)NtStatus.MediaWriteProtected, await ResolveAsync("new.txt", ReadAccess, disposition), $"disposition {disposition}");
        Assert.AreEqual((uint)NtStatus.MediaWriteProtected, await ResolveAsync("a.txt", ReadAccess, 5));
        // FILE_CREATE on an existing name: the collision comes first, as on a local volume (CREATE_NEW).
        Assert.AreEqual((uint)NtStatus.ObjectNameCollision, await ResolveAsync("a.txt", ReadAccess, 2));
        Assert.AreEqual((uint)NtStatus.ObjectNameNotFound, await ResolveAsync("new.txt", ReadAccess, 1));
        // FILE_OPEN_IF on an existing file with read access only just opens it.
        Assert.AreEqual(0u, await ResolveAsync("a.txt", ReadAccess, 3));
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

    [TestMethod]
    public async Task WatchDirectory_ChangeOnAgent_ReachesWaitChanges()
    {
        using var share = new TempFolder("pipe");
        share.WriteFile(@"proj\src\a.cs", "a"u8.ToArray());
        share.WriteFile(@"proj\other\x.cs", "x"u8.ToArray());
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = HookIpcProtocol.NewSessionId();
        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = session,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "proj")],
            CacheRoot = cacheDir.Path,
        });
        await broker.Watcher!.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await using FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));

        HookWatchReply subtree = await hook.CallOkAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = true, RelativePath = string.Empty }, HookWatchReply.Read);
        HookWatchReply flat = await hook.CallOkAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = false, RelativePath = "src" }, HookWatchReply.Read);
        Assert.AreNotEqual(subtree.WatchId, flat.WatchId);

        // Nothing changed yet: a short wait times out empty.
        HookWaitChangesReply idle = await hook.CallOkAsync(new HookWaitChangesRequest { WatchId = flat.WatchId, TimeoutMilliseconds = 50 }, HookWaitChangesReply.Read);
        Assert.IsEmpty(idle.Changes);
        Assert.IsFalse(idle.Overflow);

        File.WriteAllText(share.Combine(@"proj\src\a.cs"), "changed");
        share.WriteFile(@"proj\other\new.cs", [1]);

        HookWaitChangesReply flatChanges = await hook.CallOkAsync(new HookWaitChangesRequest { WatchId = flat.WatchId, TimeoutMilliseconds = 5000 }, HookWaitChangesReply.Read);
        Assert.Contains(new HookChange(3 /* FILE_ACTION_MODIFIED */, "a.cs"), flatChanges.Changes.ToList());
        Assert.IsFalse(flatChanges.Changes.Any(x => x.Name.Contains("new.cs")), "Flat watch must not see other folders.");

        var seen = new List<HookChange>();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!seen.Contains(new HookChange(1 /* FILE_ACTION_ADDED */, @"other\new.cs")) && DateTime.UtcNow < deadline)
            seen.AddRange((await hook.CallOkAsync(new HookWaitChangesRequest { WatchId = subtree.WatchId, TimeoutMilliseconds = 500 }, HookWaitChangesReply.Read)).Changes);
        Assert.Contains(new HookChange(1, @"other\new.cs"), seen);
        Assert.Contains(new HookChange(3, @"src\a.cs"), seen);

        // A rescan on the Agent reaches the hook as an overflow.
        agent.GetWatcher(new ReadOnlyShare("src", share.Path)).OnError(new InternalBufferOverflowException());
        HookWaitChangesReply overflow = await hook.CallOkAsync(new HookWaitChangesRequest { WatchId = flat.WatchId, TimeoutMilliseconds = 5000 }, HookWaitChangesReply.Read);
        Assert.IsTrue(overflow.Overflow);

        await hook.CallOkAsync(new HookUnwatchRequest { WatchId = flat.WatchId }, (ref HookIpcReader _) => 0);
        (uint status, _) = await hook.CallAsync(new HookWaitChangesRequest { WatchId = flat.WatchId, TimeoutMilliseconds = 0 });
        Assert.AreEqual((uint)NtStatus.InvalidHandle, status);

        (uint fileStatus, _) = await hook.CallAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = false, RelativePath = @"src\a.cs" });
        Assert.AreEqual((uint)NtStatus.NotADirectory, fileStatus);
    }

    [TestMethod]
    public async Task WaitChanges_Blocked_ReturnsWhenUnwatchedFromAnotherInstance()
    {
        using var share = new TempFolder("pipe");
        Directory.CreateDirectory(share.Combine("proj"));
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = HookIpcProtocol.NewSessionId();
        await using BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = session,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "proj")],
            CacheRoot = cacheDir.Path,
        });
        // The rescan that follows the subscription of the change watcher would overflow a watch registered before it.
        await broker.Watcher!.WaitActiveAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        await using FakeHook waiter = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
        await using FakeHook control = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));

        HookWatchReply watch = await control.CallOkAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = true, RelativePath = string.Empty }, HookWatchReply.Read);
        Task<HookWaitChangesReply> pending = waiter.CallOkAsync(new HookWaitChangesRequest { WatchId = watch.WatchId, TimeoutMilliseconds = HookIpcProtocol.InfiniteTimeout }, HookWaitChangesReply.Read);
        await Task.Delay(100);
        Assert.IsFalse(pending.IsCompleted);

        await control.CallOkAsync(new HookUnwatchRequest { WatchId = watch.WatchId }, (ref HookIpcReader _) => 0);
        HookWaitChangesReply reply = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsEmpty(reply.Changes);
    }

    private static async Task<BrokerHost> StartBrokerAsync(TempFolder share, TempFolder cacheDir, AgentServer agent, string session)
    {
        Directory.CreateDirectory(share.Combine("proj"));
        await Task.CompletedTask;
        return BrokerHost.Start(new BrokerOptions
        {
            SessionId = session,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "src",
            Mappings = [new VirtualMapping(VirtualRoot, "proj")],
            CacheRoot = cacheDir.Path,
        });
    }

    private static async Task WaitForCountAsync(HookWatchRegistry registry, int expected)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (registry.Count != expected)
        {
            Assert.IsLessThan(5000, watch.ElapsedMilliseconds, $"Watch count stayed {registry.Count}, expected {expected}.");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public async Task ClientClosesPipeDuringLongPoll_EndsTheWait_AndRemovesItsWatches()
    {
        using var share = new TempFolder("pipe");
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = HookIpcProtocol.NewSessionId();
        await using BrokerHost broker = await StartBrokerAsync(share, cacheDir, agent, session);
        HookWatchRegistry registry = broker.Pipe.Handler.Watches;

        FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
        HookWatchReply watch = await hook.CallOkAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = true, RelativePath = string.Empty }, HookWatchReply.Read);
        Task pending = hook.CallAsync(new HookWaitChangesRequest { WatchId = watch.WatchId, TimeoutMilliseconds = HookIpcProtocol.InfiniteTimeout });
        await Task.Delay(100);
        Assert.AreEqual(1, registry.Count);

        await hook.DisposeAsync();
        await WaitForCountAsync(registry, 0);
        try
        {
            await pending;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task Watch_BelongsToItsProcess_AndSurvivesOtherConnectionsOfIt()
    {
        using var share = new TempFolder("pipe");
        using var cacheDir = new TempFolder("cache");
        await using var agent = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string session = HookIpcProtocol.NewSessionId();
        await using BrokerHost broker = await StartBrokerAsync(share, cacheDir, agent, session);
        HookRequestHandler handler = broker.Pipe.Handler;

        await using FakeHook hook = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
        HookWatchReply watch = await hook.CallOkAsync(new HookWatchRequest { MappingIndex = 0, WatchSubtree = true, RelativePath = string.Empty }, HookWatchReply.Read);

        // Another process (another client id) can neither wait on, remove nor drop the watch by disconnecting.
        const long Stranger = 4242;
        handler.ClientConnected(Stranger);
        Assert.AreEqual((uint)NtStatus.InvalidHandle, await CallHandlerAsync(handler, Stranger, new HookWaitChangesRequest { WatchId = watch.WatchId, TimeoutMilliseconds = 0 }));
        Assert.AreEqual((uint)NtStatus.InvalidHandle, await CallHandlerAsync(handler, Stranger, new HookUnwatchRequest { WatchId = watch.WatchId }));
        handler.ClientDisconnected(Stranger);
        Assert.AreEqual(1, handler.Watches.Count);

        // A second pipe of the owning process closing leaves the watch alone as long as the first pipe is open.
        FakeHook second = await FakeHook.ConnectAsync(HookIpcProtocol.GetPipeName(session));
        await second.CallOkAsync(new HookHelloRequest { ProcessId = 1, Architecture = HookArchitecture.X64 }, HookHelloReply.Read);
        await second.DisposeAsync();
        await Task.Delay(100);
        Assert.AreEqual(1, handler.Watches.Count);
        await hook.CallOkAsync(new HookWaitChangesRequest { WatchId = watch.WatchId, TimeoutMilliseconds = 0 }, HookWaitChangesReply.Read);
    }

    private static async Task<uint> CallHandlerAsync(HookRequestHandler handler, long clientId, IHookIpcMessage request)
    {
        var requestBuffer = new byte[HookIpcProtocol.MaxMessageSize];
        var reply = new byte[HookIpcProtocol.MaxMessageSize];
        int size = HookIpcCodec.Encode(requestBuffer, 1, request);
        int replySize = await handler.HandleAsync(clientId, requestBuffer.AsMemory(0, size), reply, CancellationToken.None);
        return HookIpcCodec.ReadHeader(reply.AsSpan(0, replySize)).Status;
    }
}
