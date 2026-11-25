using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;
using ProcBridge.Tests.Lan;
using ProcBridge.Tests.Relay;
using ProcBridge.Transport.Relay;

namespace ProcBridge.Tests.Protocol;

/// <summary>
/// Dead connection detection (keepalive): a peer that stops answering is found by a Ping probe instead of making every
/// request wait for <see cref="TransportOptions.RequestTimeout"/>, on TCP and through a relay lane alike; the Agent
/// answers pings and drops a client that went silent.
/// </summary>
[TestClass]
public sealed class KeepAliveTests
{
    private static readonly HelloRequest Hello = new() { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "s" };

    private static readonly TransportOptions FastKeepAlive = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        RequestTimeout = TimeSpan.FromSeconds(60),
        KeepAliveInterval = TimeSpan.FromMilliseconds(200),
        KeepAliveTimeout = TimeSpan.FromMilliseconds(400),
    };

    /// <summary>Answers Hello, then reads every frame and never answers again: a hung Agent.</summary>
    private static async Task ServeSilentlyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var channel = new StreamFrameChannel(stream, ownsStream: false, FastKeepAlive.MaxFrameSize);
        Frame hello = (await channel.ReceiveAsync(cancellationToken))!.Value;
        var response = new HelloResponse { ProtocolVersion = ProtocolInfo.Version, ServerName = "hung", MaxFrameSize = FastKeepAlive.MaxFrameSize, MaxReadSize = 1024 };
        await channel.SendAsync(new Frame { Kind = FrameKind.Response, MessageType = MessageType.Hello, RequestId = hello.RequestId, Header = ProtocolSerializer.Serialize(response) }, cancellationToken);
        try
        {
            while (await channel.ReceiveAsync(cancellationToken) is not null)
            {
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ProtocolStatusException)
        {
        }
    }

    [TestMethod]
    public async Task SilentPeer_IsDeclaredDead_AndPendingRequestFailsAsConnectionFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task server = Task.Run(async () =>
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync();
                await ServeSilentlyAsync(peer.GetStream(), stop.Token);
            });

            await using ProtocolConnection connection = await ProtocolConnection.OpenAsync(new TcpConnectionFactory("127.0.0.1", port, FastKeepAlive), Hello, FastKeepAlive, CancellationToken.None);
            var watch = Stopwatch.StartNew();
            var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(
                () => connection.SendAsync(MessageType.FsStat, ProtocolSerializer.Serialize(new FsStatRequest { Path = "x" }), default, CancellationToken.None));

            // Far below the 60 s request timeout, and retryable on another connection by the pool.
            Assert.IsTrue(e.IsConnectionFailure, e.Message);
            Assert.IsLessThan(10_000, watch.ElapsedMilliseconds);
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
    public async Task SilentPeerThroughRelay_IsDeclaredDead()
    {
        await using var relay = InProcessRelay.Start();
        using var agentKey = IdentityKey.Generate();
        using var stop = new CancellationTokenSource();
        var lanes = new List<Task>();
        await using var host = new RelayHostLink(relay.Endpoint, agentKey, (lane, _) =>
        {
            lock (lanes)
                lanes.Add(Task.Run(async () =>
                {
                    await using (lane)
                        await ServeSilentlyAsync(lane, stop.Token);
                }));
            return Task.CompletedTask;
        }, relay.Sockets);
        host.Start();
        await relay.WaitOnlineAsync(agentKey.Fingerprint);
        using var connector = new RelayStreamConnector(relay.Endpoint, agentKey.Fingerprint, relay.Sockets, relay.Factory.CreateClient());

        await using ProtocolConnection connection = await ProtocolConnection.OpenAsync(new LaneFactory(connector), Hello, FastKeepAlive, CancellationToken.None);
        // The lane itself stays open: only the missing answers reveal the hung peer.
        Task closed = connection.Closed;
        Assert.AreSame(closed, await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(10))), "keepalive did not notice the silent peer behind the relay");
        Assert.IsFalse(connection.IsAlive);

        stop.Cancel();
        Task[] serving;
        lock (lanes)
            serving = [.. lanes];
        await Task.WhenAll(serving);
    }

    [TestMethod]
    public async Task Agent_AnswersPings_SoAnIdleConnectionStaysAlive()
    {
        using var share = new TempFolder("agent");
        await using var server = new AgentServer([new AgentShare("s", share.Path)], new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0 });
        server.Start();

        await using ProtocolConnection connection = await ProtocolConnection.OpenAsync(new TcpConnectionFactory("127.0.0.1", server.Port, FastKeepAlive), Hello, FastKeepAlive, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.IsTrue(connection.IsAlive, "the Agent did not answer the keepalive probes");
        Frame ping = await connection.SendAsync(MessageType.Ping, default, default, CancellationToken.None);
        Assert.AreEqual(FrameKind.Response, ping.Kind);
    }

    [TestMethod]
    public async Task Agent_ClosesAConnectionThatWentSilent()
    {
        using var share = new TempFolder("agent");
        var options = new AgentOptions { AllowPlaintextTransport = true, BindAddress = IPAddress.Loopback, Port = 0, IdleTimeout = TimeSpan.FromMilliseconds(500) };
        await using var server = new AgentServer([new AgentShare("s", share.Path)], options);
        server.Start();

        // Keepalive off on this side: after Hello the client sends nothing at all.
        var silent = FastKeepAlive with { KeepAliveInterval = TimeSpan.Zero };
        await using ProtocolConnection connection = await ProtocolConnection.OpenAsync(new TcpConnectionFactory("127.0.0.1", server.Port, silent), Hello, silent, CancellationToken.None);

        Task closed = connection.Closed;
        Assert.AreSame(closed, await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(10))), "the Agent kept a silent connection");
    }

    private sealed class LaneFactory(RelayStreamConnector connector) : IConnectionFactory
    {
        public string Description => "relay lane";

        public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
            => new StreamFrameChannel(await connector.ConnectAsync(cancellationToken), ownsStream: true, FastKeepAlive.MaxFrameSize);
    }
}
