using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Lan;

/// <summary>Agent on loopback driven by the raw connection pool (no Broker).</summary>
[TestClass]
public sealed class AgentServerTests
{
    private static async Task<T> SendAsync<T>(ConnectionPool pool, MessageType type, object request)
    {
        byte[] header = request switch
        {
            FsStatRequest x => ProtocolSerializer.Serialize(x),
            FsListRequest x => ProtocolSerializer.Serialize(x),
            FsReadRequest x => ProtocolSerializer.Serialize(x),
            _ => throw new ArgumentException(null, nameof(request)),
        };
        Frame frame = await pool.SendAsync(type, header, default, idempotent: true, CancellationToken.None);
        return ProtocolSerializer.Deserialize<T>(frame.Header);
    }

    private static async Task<NtStatus> StatusOfStatAsync(ConnectionPool pool, string path)
    {
        try
        {
            await SendAsync<FsStatResponse>(pool, MessageType.FsStat, new FsStatRequest { Path = path });
            return NtStatus.Success;
        }
        catch (ProtocolStatusException e)
        {
            return e.Status;
        }
    }

    private static (AgentServer Server, ConnectionPool Pool) Start(TempFolder share, string shareName = "src", string requestedShare = "src")
    {
        var server = new AgentServer([new AgentShare(shareName, share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, MaxReadSize = 64 * 1024 });
        server.Start();
        var hello = new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = requestedShare };
        var pool = new ConnectionPool(new TcpConnectionFactory("127.0.0.1", server.Port, new TransportOptions()), hello, new TransportOptions());
        return (server, pool);
    }

    [TestMethod]
    public async Task Client_NotReadingResponses_IsDroppedAfterSendTimeout()
    {
        using var share = new TempFolder("agent");
        share.WriteFile("big.bin", new byte[1024 * 1024]);
        var options = new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, SendTimeout = TimeSpan.FromMilliseconds(500) };
        await using var server = new AgentServer([new AgentShare("src", share.Path)], options);
        server.Start();

        using var client = new System.Net.Sockets.TcpClient { ReceiveBufferSize = 4096 };
        await client.ConnectAsync(IPAddress.Loopback, server.Port);
        await using var channel = new StreamFrameChannel(client.GetStream(), ownsStream: false, options.MaxFrameSize);
        var hello = new HelloRequest { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "src" };
        await channel.SendAsync(new Frame { Kind = FrameKind.Request, MessageType = MessageType.Hello, RequestId = 1, Header = ProtocolSerializer.Serialize(hello) }, CancellationToken.None);
        Assert.IsNotNull(await channel.ReceiveAsync(CancellationToken.None));

        // Ask for far more data than the socket buffers hold, then never read it.
        byte[] read = ProtocolSerializer.Serialize(new FsReadRequest { Path = "big.bin", Offset = 0, Length = 1024 * 1024 });
        for (uint id = 2; id < 80; id++)
            await channel.SendAsync(new Frame { Kind = FrameKind.Request, MessageType = MessageType.FsRead, RequestId = id, Header = read }, CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (server.ActiveSessionCount > 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        Assert.AreEqual(0, server.ActiveSessionCount);
    }

    [TestMethod]
    public async Task ClientsThatDisconnectAtOnce_LeaveNoSessionBehind()
    {
        using var share = new TempFolder("agent");
        await using var server = new AgentServer([new AgentShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();
        for (int i = 0; i < 200; i++)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, server.Port);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (server.ActiveSessionCount > 0 && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        Assert.AreEqual(0, server.ActiveSessionCount);
    }

    [TestMethod]
    public async Task StatListRead_Work()
    {
        using var share = new TempFolder("agent");
        share.WriteFile("a.txt", "hello"u8.ToArray());
        string hidden = share.WriteFile(@"dir\hidden.bin", [1, 2, 3]);
        File.SetAttributes(hidden, FileAttributes.Hidden);
        (AgentServer server, ConnectionPool pool) = Start(share);
        await using (server)
        await using (pool)
        {
            FsStatResponse root = await SendAsync<FsStatResponse>(pool, MessageType.FsStat, new FsStatRequest { Path = "" });
            Assert.IsTrue(root.Entry.IsDirectory);
            Assert.AreEqual(string.Empty, root.Entry.Name);

            FsStatResponse file = await SendAsync<FsStatResponse>(pool, MessageType.FsStat, new FsStatRequest { Path = "a.txt" });
            Assert.AreEqual(5L, file.Entry.Size);
            Assert.AreEqual(File.GetLastWriteTimeUtc(share.Combine("a.txt")).ToFileTimeUtc(), file.Entry.LastWriteTime);

            FsListResponse list = await SendAsync<FsListResponse>(pool, MessageType.FsList, new FsListRequest { Path = "dir" });
            Assert.HasCount(1, list.Entries);
            Assert.AreEqual("hidden.bin", list.Entries[0].Name);
            Assert.AreNotEqual(0u, list.Entries[0].Attributes & (uint)FileAttributes.Hidden);

            Frame read = await pool.SendAsync(MessageType.FsRead, ProtocolSerializer.Serialize(new FsReadRequest { Path = "a.txt", Offset = 1, Length = 100 }), default, true, CancellationToken.None);
            CollectionAssert.AreEqual("ello"u8.ToArray(), read.Payload.ToArray());
            Assert.AreEqual(file.Entry.Version, ProtocolSerializer.Deserialize<FsReadResponse>(read.Header).Version);
        }
    }

    [TestMethod]
    public async Task Errors_MapToNtStatus()
    {
        using var share = new TempFolder("agent");
        share.WriteFile("a.txt", "x"u8.ToArray());
        (AgentServer server, ConnectionPool pool) = Start(share);
        await using (server)
        await using (pool)
        {
            Assert.AreEqual(NtStatus.ObjectNameNotFound, await StatusOfStatAsync(pool, "missing.txt"));
            Assert.AreEqual(NtStatus.ObjectPathNotFound, await StatusOfStatAsync(pool, "nodir/missing.txt"));

            var notDir = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => SendAsync<FsListResponse>(pool, MessageType.FsList, new FsListRequest { Path = "a.txt" }));
            Assert.AreEqual(NtStatus.NotADirectory, notDir.Status);

            var tooBig = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => SendAsync<FsReadResponse>(pool, MessageType.FsRead, new FsReadRequest { Path = "a.txt", Offset = 0, Length = 1024 * 1024 }));
            Assert.AreEqual(NtStatus.InvalidParameter, tooBig.Status);

            var changed = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => SendAsync<FsReadResponse>(pool, MessageType.FsRead,
                new FsReadRequest { Path = "a.txt", Offset = 0, Length = 1, ExpectedVersion = new FsVersion { LastWriteTime = 1, Size = 1 } }));
            Assert.AreEqual(NtStatus.FileVersionChanged, changed.Status);
        }
    }

    [TestMethod]
    [DataRow("..")]
    [DataRow("../outside.txt")]
    [DataRow(@"..\outside.txt")]
    [DataRow("sub/../../outside.txt")]
    [DataRow("C:/Windows/win.ini")]
    [DataRow("a.txt:stream")]
    [DataRow("a.txt.")]
    public async Task PathTraversal_IsRejected(string path)
    {
        using var parent = new TempFolder("agent-parent");
        parent.WriteFile("outside.txt", "secret"u8.ToArray());
        using var share = new TempFolder("agent");
        share.WriteFile("a.txt", "x"u8.ToArray());
        Directory.CreateDirectory(share.Combine("sub"));
        (AgentServer server, ConnectionPool pool) = Start(share);
        await using (server)
        await using (pool)
        {
            NtStatus status = await StatusOfStatAsync(pool, path);
            Assert.IsTrue(status is NtStatus.ObjectNameInvalid or NtStatus.AccessDenied, $"{path} -> {status}");
        }
    }

    [TestMethod]
    public async Task JunctionOutsideShare_IsRejected_InsideIsAllowed()
    {
        using var outside = new TempFolder("agent-outside");
        outside.WriteFile("secret.txt", "secret"u8.ToArray());
        using var share = new TempFolder("agent");
        share.WriteFile(@"real\inner.txt", "inner"u8.ToArray());
        share.CreateJunction("escape", outside.Path);
        share.CreateJunction("alias", share.Combine("real"));
        (AgentServer server, ConnectionPool pool) = Start(share);
        await using (server)
        await using (pool)
        {
            Assert.AreEqual(NtStatus.AccessDenied, await StatusOfStatAsync(pool, "escape/secret.txt"));
            Assert.AreEqual(NtStatus.AccessDenied, await StatusOfStatAsync(pool, "escape"));
            var list = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => SendAsync<FsListResponse>(pool, MessageType.FsList, new FsListRequest { Path = "escape" }));
            Assert.AreEqual(NtStatus.AccessDenied, list.Status);

            Assert.AreEqual(NtStatus.Success, await StatusOfStatAsync(pool, "alias/inner.txt"));
        }
    }

    [TestMethod]
    public async Task UnknownShare_IsBadNetworkName()
    {
        using var share = new TempFolder("agent");
        (AgentServer server, ConnectionPool pool) = Start(share, requestedShare: "other");
        await using (server)
        await using (pool)
        {
            var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => SendAsync<FsStatResponse>(pool, MessageType.FsStat, new FsStatRequest { Path = "" }));
            Assert.AreEqual(NtStatus.BadNetworkName, e.Status);
        }
    }
}
