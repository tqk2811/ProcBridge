using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Models;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Tests.Secure;

/// <summary>Limits on connections that have not authenticated yet, single pairing, pairing switched off, client handshake timeout.</summary>
[TestClass]
public sealed class HandshakeLimitTests
{
    [TestMethod]
    public async Task PendingHandshakes_AreLimitedPerAddress_AndGlobally()
    {
        using var key = IdentityKey.Generate();
        using var server = new SecureServer(key, new PairedMachineStore(), new SecureServerOptions
        {
            HandshakeTimeout = TimeSpan.FromSeconds(30),
            MaxPendingHandshakes = 3,
            MaxPendingHandshakesPerAddress = 2,
        });
        using var cts = new CancellationTokenSource();
        var silent = new List<SilentStream>();
        var pending = new List<Task<SecureFrameChannel?>>();
        Task<SecureFrameChannel?> Start(string address, CancellationToken token)
        {
            var stream = new SilentStream();
            silent.Add(stream);
            Task<SecureFrameChannel?> task = server.AcceptAsync(stream, address, token);
            pending.Add(task);
            return task;
        }

        _ = Start("10.0.0.1:1000", cts.Token);
        _ = Start("10.0.0.1:1001", cts.Token);
        // A third from the same address (another source port) is closed at once.
        Task<SecureFrameChannel?> overAddress = Start("10.0.0.1:1002", cts.Token);
        Assert.IsNull(await overAddress.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(silent[2].IsDisposed);

        _ = Start("10.0.0.2:2000", cts.Token);
        // The global limit (3) is reached: even a new address is closed at once.
        Task<SecureFrameChannel?> overGlobal = Start("10.0.0.3:3000", cts.Token);
        Assert.IsNull(await overGlobal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(silent[4].IsDisposed);
        Assert.IsFalse(silent[0].IsDisposed);

        // Slots come back when the pending ones end.
        await cts.CancelAsync();
        foreach (Task<SecureFrameChannel?> task in pending)
            Assert.IsNull(await task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var again = new CancellationTokenSource();
        Task<SecureFrameChannel?> reuse = Start("10.0.0.1:1100", again.Token);
        await Task.Delay(100);
        Assert.IsFalse(silent[^1].IsDisposed, "A slot was not given back.");
        await again.CancelAsync();
        await reuse;
    }

    [TestMethod]
    public async Task OnlyOnePairingWaitsAtATime()
    {
        using var share = new TempFolder("pair-limit");
        await using var m = new PairingMachines(share);
        var requests = new List<PairingRequest>();
        var first = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request =>
        {
            lock (requests)
                requests.Add(request);
            first.TrySetResult(request);
        };

        Task<PairedMachine> pairing = m.Client.PairAsync(m.Connector, async (_, _) => (await first.Task).Code, CancellationToken.None);
        PairingRequest firstRequest = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var otherKey = IdentityKey.Generate();
        var other = new PairingClient(otherKey, new PairedMachineStore(), "machine-c");
        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => other.PairAsync(m.Connector, (_, _) => Task.FromResult("000000"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        lock (requests)
            Assert.HasCount(1, requests);

        // The first one is untouched and still completes.
        firstRequest.Accept();
        await pairing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(m.AgentTrust.IsTrusted(m.BrokerKey.PublicKey));
    }

    [TestMethod]
    public async Task TurningPairingOff_CancelsThePendingPairing_AndStoresNothing()
    {
        using var share = new TempFolder("pair-limit");
        await using var m = new PairingMachines(share);
        var first = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request => first.TrySetResult(request);

        Task<PairedMachine> pairing = m.Client.PairAsync(m.Connector, async (_, _) => (await first.Task).Code, CancellationToken.None);
        PairingRequest request = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        m.Server.AllowPairing = false;
        request.Accept();

        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => pairing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsFalse(await request.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsEmpty(m.AgentTrust.List());
        Assert.IsEmpty(m.BrokerTrust.List());
    }

    [TestMethod]
    public async Task Client_GivesUpOnAnAgentThatAcceptsAndSaysNothing()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task<System.Net.Sockets.TcpClient> serverSide = listener.AcceptTcpClientAsync();
        using var agentKey = IdentityKey.Generate();
        using var brokerKey = IdentityKey.Generate();
        var trust = new PairedMachineStore();
        trust.Add(agentKey.PublicKey, "machine-b");
        var options = new TransportOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(500), ConnectTimeout = TimeSpan.FromSeconds(60) };
        using var factory = new SecureConnectionFactory(new TcpStreamConnector("127.0.0.1", port), brokerKey, trust, agentKey.PublicKey, options);

        DateTime started = DateTime.UtcNow;
        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(async () => await factory.ConnectAsync(CancellationToken.None));
        Assert.IsTrue(e.IsConnectionFailure);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), DateTime.UtcNow - started);
        (await serverSide).Dispose();
    }

    private sealed class PairingMachines : IAsyncDisposable
    {
        public PairingMachines(TempFolder share)
        {
            Server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, PairingTimeout = TimeSpan.FromSeconds(20) },
                new AgentSecurity { Identity = AgentKey, PairedMachines = AgentTrust, AllowPairing = true });
            Server.Start();
            Client = new PairingClient(BrokerKey, BrokerTrust, "machine-a");
        }

        public IdentityKey AgentKey { get; } = IdentityKey.Generate();
        public IdentityKey BrokerKey { get; } = IdentityKey.Generate();
        public PairedMachineStore AgentTrust { get; } = new();
        public PairedMachineStore BrokerTrust { get; } = new();
        public AgentServer Server { get; }
        public PairingClient Client { get; }

        public TcpStreamConnector Connector => new("127.0.0.1", Server.Port);

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            AgentKey.Dispose();
            BrokerKey.Dispose();
        }
    }

    /// <summary>A connection that never sends a byte: reads wait for cancellation or dispose.</summary>
    private sealed class SilentStream : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed => _closed.Task.IsCompleted;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
    }
}
