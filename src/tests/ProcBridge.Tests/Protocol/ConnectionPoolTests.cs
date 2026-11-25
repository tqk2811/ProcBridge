using System.Net;
using System.Net.Sockets;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;

namespace ProcBridge.Tests.Protocol;

[TestClass]
public sealed class ConnectionPoolTests
{
    private static readonly HelloRequest Hello = new() { ProtocolVersion = ProtocolInfo.Version, ClientName = "test", ShareName = "s" };

    /// <summary>Loopback server that answers Hello at once and every other request after <c>latency</c>, with an empty response.</summary>
    private sealed class FakePeer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TimeSpan _latency;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _acceptLoop;
        private int _closedConnections;

        public FakePeer(TimeSpan latency)
        {
            _latency = latency;
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int ClosedConnections => Volatile.Read(ref _closedConnections);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (true)
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                await using var channel = new StreamFrameChannel(client.GetStream(), ownsStream: false, new TransportOptions().MaxFrameSize);
                try
                {
                    while (await channel.ReceiveAsync(_stop.Token) is { } request)
                    {
                        Frame current = request;
                        _ = Task.Run(async () =>
                        {
                            Memory<byte> header = Memory<byte>.Empty;
                            if (current.MessageType == MessageType.Hello)
                                header = ProtocolSerializer.Serialize(new HelloResponse { ProtocolVersion = ProtocolInfo.Version, ServerName = "fake", MaxFrameSize = 1 << 20, MaxReadSize = 1 << 16 });
                            else
                                await Task.Delay(_latency);
                            try
                            {
                                await channel.SendAsync(new Frame { Kind = FrameKind.Response, MessageType = current.MessageType, RequestId = current.RequestId, Header = header }, CancellationToken.None);
                            }
                            catch (Exception e) when (e is IOException or ObjectDisposedException)
                            {
                            }
                        });
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or ProtocolStatusException)
                {
                }
            }
            Interlocked.Increment(ref _closedConnections);
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _acceptLoop;
        }
    }

    /// <summary>Factory whose n-th connect (1 based) can be delayed or made to fail.</summary>
    private sealed class ScriptedFactory(int port, Func<int, Task> beforeConnect) : IConnectionFactory
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public string Description => "scripted";

        public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
        {
            await beforeConnect(Interlocked.Increment(ref _attempts));
            var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            return new StreamFrameChannel(client.GetStream(), ownsStream: true, new TransportOptions().MaxFrameSize);
        }
    }

    private static Task<Frame> RequestAsync(ConnectionPool pool)
        => pool.SendAsync(MessageType.FsStat, ProtocolSerializer.Serialize(new FsStatRequest { Path = "" }), default, idempotent: true, CancellationToken.None);

    [TestMethod]
    public async Task BusyConnection_IsNotBlockedByAnotherRequestOpeningASlot()
    {
        await using var peer = new FakePeer(TimeSpan.FromMilliseconds(300));
        var options = new TransportOptions { ConnectionCount = 2, ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10) };
        var factory = new ScriptedFactory(peer.Port, attempt => attempt == 2 ? Task.Delay(3000) : Task.CompletedTask);
        await using var pool = new ConnectionPool(factory, Hello, options);
        await RequestAsync(pool); // first connection is up

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Task<long>[] requests = Enumerable.Range(0, 10).Select(async _ =>
        {
            await RequestAsync(pool);
            return watch.ElapsedMilliseconds;
        }).ToArray();
        long[] finished = await Task.WhenAll(requests);

        // At most the request that started the slow connect may wait for it; the others use the live connection.
        Assert.IsGreaterThanOrEqualTo(8, finished.Count(x => x < 2000), string.Join(",", finished));
    }

    [TestMethod]
    public async Task FailedSlotConnect_IsNotRetriedOnEveryRequest()
    {
        await using var peer = new FakePeer(TimeSpan.FromMilliseconds(100));
        var options = new TransportOptions { ConnectionCount = 2, ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10) };
        var factory = new ScriptedFactory(peer.Port, async attempt =>
        {
            if (attempt > 1)
            {
                await Task.Delay(50);
                throw new IOException("connect refused");
            }
        });
        await using var pool = new ConnectionPool(factory, Hello, options);
        await RequestAsync(pool);

        for (int wave = 0; wave < 5; wave++)
            await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RequestAsync(pool)));

        // One failed attempt for the second slot, then the slot rests instead of costing every busy request a connect.
        Assert.IsLessThanOrEqualTo(2, factory.Attempts, $"attempts {factory.Attempts}");
    }

    [TestMethod]
    public async Task DisposeWhileConnecting_ClosesTheNewConnection()
    {
        await using var peer = new FakePeer(TimeSpan.FromMilliseconds(10));
        var options = new TransportOptions { ConnectionCount = 1, ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10) };
        var factory = new ScriptedFactory(peer.Port, _ => Task.Delay(600));
        var pool = new ConnectionPool(factory, Hello, options);

        Task<Frame> request = RequestAsync(pool);
        await Task.Delay(150); // connect is in progress
        await pool.DisposeAsync();
        try
        {
            await request;
        }
        catch (Exception e) when (e is ProtocolStatusException or ObjectDisposedException)
        {
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (peer.ClosedConnections == 0 && watch.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(50);
        Assert.AreEqual(1, peer.ClosedConnections);
    }
}
