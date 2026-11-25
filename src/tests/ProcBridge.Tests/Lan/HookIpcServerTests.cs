using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.HookIpc.Enums;
using ProcBridge.HookIpc.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

[TestClass]
public sealed class HookIpcServerTests
{
    private static HookRequestHandler CreateHandler(TempFolder cacheDir)
    {
        // The handler is never reached: these tests only open and close pipe connections.
        var client = new AgentClient(new ProcBridge.Protocol.Transport.TcpConnectionFactory("127.0.0.1", 1, new ProcBridge.Protocol.Models.TransportOptions()), "src", new ProcBridge.Protocol.Models.TransportOptions());
        return new HookRequestHandler(new FileCache(client, cacheDir.Path, TimeSpan.Zero), []);
    }

    private static async Task ConnectAsync(string pipeName)
    {
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
    }

    [TestMethod]
    public async Task SecondServerOnTheSamePipeName_IsRefused()
    {
        using var cacheDir = new TempFolder("cache");
        string name = HookIpcProtocol.GetPipeName(HookIpcProtocol.NewSessionId());
        await using var first = new HookIpcServer(name, CreateHandler(cacheDir));
        first.Start();
        await using var squatter = new HookIpcServer(name, CreateHandler(cacheDir));
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => squatter.Start());
    }

    [TestMethod]
    public void SessionIds_AreRandomHex()
    {
        string[] ids = Enumerable.Range(0, 50).Select(_ => HookIpcProtocol.NewSessionId()).ToArray();
        Assert.HasCount(50, ids.Distinct());
        foreach (string id in ids)
            Assert.AreEqual(32, id.Length);
        Assert.IsTrue(ids.All(x => x.All(Uri.IsHexDigit)));
    }

    [TestMethod]
    public async Task FailingInstanceCreation_IsRetriedInsteadOfEndingTheAcceptLoop()
    {
        using var cacheDir = new TempFolder("cache");
        string name = HookIpcProtocol.GetPipeName(HookIpcProtocol.NewSessionId());
        int created = 0;
        int failures = 0;
        NamedPipeServerStream Create(bool first)
        {
            // The first instance and the one after the first client succeed; the next two creations fail.
            int n = Interlocked.Increment(ref created);
            if (n is 3 or 4)
            {
                Interlocked.Increment(ref failures);
                throw new IOException("All pipe instances are busy.");
            }
            return HookIpcServer.CreateDefaultInstance(name, first);
        }

        await using var server = new HookIpcServer(name, CreateHandler(cacheDir), Create);
        server.Start();
        await ConnectAsync(name);
        await ConnectAsync(name);
        await ConnectAsync(name);
        Assert.AreEqual(2, Volatile.Read(ref failures));
    }

    /// <summary>
    /// A request that takes longer than the hook's call timeout (here machine B answers a stat after 1.5 s) gets progress
    /// messages while it runs, then its reply: the hook waits from the last message instead of failing the call.
    /// </summary>
    [TestMethod]
    public async Task SlowRequest_SendsProgressMessagesBeforeTheReply()
    {
        (int progress, uint status) = await RunSlowStatAsync(TimeSpan.FromMilliseconds(200), null);
        Assert.AreEqual(0u, status);
        Assert.IsGreaterThanOrEqualTo(3, progress);
    }

    /// <summary>
    /// Progress stops after the configured cap even though the request still runs: a request stuck in the Broker must
    /// end with the hook's call timeout instead of keeping the hooked thread waiting forever.
    /// </summary>
    [TestMethod]
    public async Task SlowRequest_StopsSendingProgressAfterTheCap()
    {
        (int progress, uint status) = await RunSlowStatAsync(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(0u, status);
        Assert.IsLessThanOrEqualTo(3, progress);
    }

    /// <summary>Machine B answers a stat after 1.5 s; returns the progress messages seen before the reply and its status.</summary>
    private static async Task<(int Progress, uint Status)> RunSlowStatAsync(TimeSpan progressInterval, TimeSpan? maxProgressDuration)
    {
        using var cacheDir = new TempFolder("cache");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        Task agent = Task.Run(async () =>
        {
            // A slow machine B: answers Hello at once and every request after 1.5 s.
            using TcpClient peer = await listener.AcceptTcpClientAsync(stop.Token);
            var options = new TransportOptions();
            await using var channel = new StreamFrameChannel(peer.GetStream(), ownsStream: false, options.MaxFrameSize);
            try
            {
                while (await channel.ReceiveAsync(stop.Token) is { } frame)
                {
                    byte[] header = frame.MessageType switch
                    {
                        MessageType.Hello => ProtocolSerializer.Serialize(new HelloResponse { ProtocolVersion = ProtocolInfo.Version, ServerName = "slow", MaxFrameSize = options.MaxFrameSize, MaxReadSize = 1024 }),
                        MessageType.FsStat => ProtocolSerializer.Serialize(new FsStatResponse { Entry = new FsEntry { Name = "a.txt", Attributes = 0x80, Size = 1, CreationTime = 0, LastAccessTime = 0, LastWriteTime = 0, ChangeTime = 0 } }),
                        _ => ProtocolSerializer.Serialize(new ErrorResponse { Status = NtStatus.NotImplemented, Message = "no" }),
                    };
                    if (frame.MessageType == MessageType.FsStat)
                        await Task.Delay(1500, stop.Token);
                    FrameKind kind = frame.MessageType is MessageType.Hello or MessageType.FsStat ? FrameKind.Response : FrameKind.Error;
                    await channel.SendAsync(new Frame { Kind = kind, MessageType = frame.MessageType, RequestId = frame.RequestId, Header = header }, stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or ProtocolStatusException)
            {
            }
        });

        var transport = new TransportOptions();
        var client = new AgentClient(new TcpConnectionFactory("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, transport), "src", transport);
        var handler = new HookRequestHandler(new FileCache(client, cacheDir.Path, TimeSpan.Zero), [new VirtualMapping(@"D:\virtual", "")]);
        string name = HookIpcProtocol.GetPipeName(HookIpcProtocol.NewSessionId());
        int progress = 0;
        HookIpcHeader reply;
        await using (var server = new HookIpcServer(name, handler, progressInterval: progressInterval, maxProgressDuration: maxProgressDuration))
        {
            server.Start();
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            pipe.ReadMode = PipeTransmissionMode.Message;
            byte[] buffer = new byte[HookIpcProtocol.MaxMessageSize];
            int size = HookIpcCodec.Encode(buffer, 7, new HookStatRequest { MappingIndex = 0, RelativePath = "a.txt" });
            await pipe.WriteAsync(buffer.AsMemory(0, size));

            while (true)
            {
                int length = await pipe.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                reply = HookIpcCodec.ReadHeader(buffer.AsSpan(0, length));
                Assert.AreEqual(7u, reply.RequestId);
                if (reply.Status != HookIpcProtocol.StatusInProgress)
                    break;
                Assert.AreEqual(HookIpcMessageType.Stat, reply.MessageType);
                progress++;
            }
        }
        stop.Cancel();
        listener.Stop();
        await agent;
        return (progress, reply.Status);
    }
}
