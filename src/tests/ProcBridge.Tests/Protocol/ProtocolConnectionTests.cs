using System.Net;
using System.Net.Sockets;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class ProtocolConnectionTests
{
    private static readonly HelloRequest Hello = new() { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "s" };

    /// <summary>Connects with no send buffer so a peer that stops reading blocks the writer quickly.</summary>
    private sealed class TinyBufferFactory(int port, TransportOptions options) : IConnectionFactory
    {
        public string Description => "tiny-buffer loopback";

        public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
        {
            var client = new TcpClient { NoDelay = true, SendBufferSize = 0 };
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return new StreamFrameChannel(client.GetStream(), ownsStream: true, options.MaxFrameSize);
        }
    }

    [TestMethod]
    public async Task Hello_NeverAnswered_TimesOut()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Accepts and then stays silent, like a hung Agent.
            Task<TcpClient> accept = listener.AcceptTcpClientAsync();
            var options = new TransportOptions { ConnectTimeout = TimeSpan.FromMilliseconds(500), RequestTimeout = TimeSpan.FromMilliseconds(500) };
            var factory = new TcpConnectionFactory("127.0.0.1", port, options);

            var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => ProtocolConnection.OpenAsync(factory, Hello, options, CancellationToken.None));
            Assert.AreEqual(NtStatus.IoTimeout, e.Status);
            (await accept).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task Request_PeerNotReading_TimesOutAndKillsConnection()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        using var stop = new CancellationTokenSource();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var options = new TransportOptions
            {
                MaxFrameSize = 64 * 1024 * 1024,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                RequestTimeout = TimeSpan.FromSeconds(1),
            };
            // Answers Hello, then never reads again: the request below fills the socket buffers and blocks the write.
            Task server = Task.Run(async () =>
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync();
                await using var channel = new StreamFrameChannel(peer.GetStream(), ownsStream: false, options.MaxFrameSize);
                Frame hello = (await channel.ReceiveAsync(CancellationToken.None))!.Value;
                var response = new HelloResponse { ProtocolVersion = ProtocolInfo.Version, ServerName = "stuck", MaxFrameSize = options.MaxFrameSize, MaxReadSize = 1024 };
                await channel.SendAsync(new Frame { Kind = FrameKind.Response, MessageType = MessageType.Hello, RequestId = hello.RequestId, Header = ProtocolSerializer.Serialize(response) }, CancellationToken.None);
                await Task.Delay(Timeout.Infinite, stop.Token).ContinueWith(_ => { });
            });

            var factory = new TinyBufferFactory(port, options);
            await using ProtocolConnection connection = await ProtocolConnection.OpenAsync(factory, Hello, options, CancellationToken.None);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(
                () => connection.SendAsync(MessageType.FsRead, ProtocolSerializer.Serialize(new FsReadRequest { Path = "x", Offset = 0, Length = 0 }), new byte[32 * 1024 * 1024], CancellationToken.None));
            Assert.AreEqual(NtStatus.IoTimeout, e.Status);
            Assert.IsLessThan(10_000, watch.ElapsedMilliseconds);
            // A frame was left half written, so the connection must not be reused.
            Assert.IsFalse(connection.IsAlive);
            stop.Cancel();
            await server;
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestMethod]
    public async Task Connect_NoListener_IsNetworkUnreachable()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var options = new TransportOptions { ConnectTimeout = TimeSpan.FromSeconds(5) };
        var factory = new TcpConnectionFactory("127.0.0.1", port, options);
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => ProtocolConnection.OpenAsync(factory, Hello, options, CancellationToken.None));
        Assert.IsTrue(e.IsConnectionFailure);
        Assert.AreEqual(NtStatus.NetworkUnreachable, e.Status);
    }
}
