using System.Net;
using System.Text;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Models;
using ProcBridge.Protocol.Transport;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Tests.Secure;

/// <summary>Agent and AgentClient over the encrypted transport (M5): capture on the wire, trust, revocation, sessions.</summary>
[TestClass]
public sealed class SecureTransportTests
{
    private const string Secret = "PB-SECRET-CONTENT-7f3a91c2";
    private const string SecretName = "pb-secret-name-b7e2.txt";

    private static readonly TransportOptions Transport = new()
    {
        ConnectionCount = 4,
        ReadChunkSize = 64 * 1024,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        RequestTimeout = TimeSpan.FromSeconds(10),
        MaxRetries = 0,
    };

    private static AgentServer StartAgent(TempFolder share, AgentSecurity? security, int port = 0)
    {
        var options = new AgentOptions { BindAddress = IPAddress.Loopback, Port = port, AllowPlaintextTransport = security is null };
        var server = new AgentServer([new ReadOnlyShare("src", share.Path)], options, security);
        server.Start();
        return server;
    }

    private static SecureConnectionFactory SecureFactory(int port, SecurePeers peers, Transport.Secure.Interfaces.IStreamConnector? connector = null)
        => new(connector ?? new TcpStreamConnector("127.0.0.1", port), peers.BrokerKey, peers.BrokerTrust, peers.AgentKey.PublicKey, Transport);

    private static async Task DownloadSecretAsync(AgentClient client, TempFolder target)
    {
        FsEntry entry = await client.StatAsync(SecretName, CancellationToken.None);
        string destination = target.Combine("copy.txt");
        await client.DownloadAsync(SecretName, entry, destination, CancellationToken.None);
        Assert.AreEqual(Secret, File.ReadAllText(destination));
        IReadOnlyList<FsEntry> list = await client.ListAsync(string.Empty, CancellationToken.None);
        Assert.IsTrue(list.Any(x => x.Name == SecretName));
    }

    private static bool Contains(byte[] haystack, string needle)
        => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0 || haystack.AsSpan().IndexOf(Encoding.Unicode.GetBytes(needle)) >= 0;

    [TestMethod]
    public async Task WireCapture_ShowsNoPlainContent()
    {
        using var share = new TempFolder("sec");
        share.WriteFile(SecretName, Encoding.UTF8.GetBytes(Secret));
        using var target = new TempFolder("sec-copy");
        using var peers = new SecurePeers();
        await using AgentServer server = StartAgent(share, peers.Agent());
        var tap = new WireTap(new TcpStreamConnector("127.0.0.1", server.Port));
        using SecureConnectionFactory factory = SecureFactory(server.Port, peers, tap);
        await using (var client = new AgentClient(factory, "src", Transport, clientName: "pb-client-name-91d0"))
            await DownloadSecretAsync(client, target);

        byte[] wire = tap.Captured;
        Assert.IsGreaterThan(Secret.Length, wire.Length);
        Assert.IsFalse(Contains(wire, Secret), "File content visible on the wire.");
        Assert.IsFalse(Contains(wire, SecretName), "File name visible on the wire.");
        Assert.IsFalse(Contains(wire, "pb-client-name-91d0"), "Hello visible on the wire.");
        Assert.IsFalse(Contains(wire, "src"), "Share name visible on the wire.");
    }

    [TestMethod]
    public async Task WireCapture_PlainTransport_ShowsContent()
    {
        // Control for the test above: the same capture of the development plain transport does see the data.
        using var share = new TempFolder("sec");
        share.WriteFile(SecretName, Encoding.UTF8.GetBytes(Secret));
        using var target = new TempFolder("sec-copy");
        await using AgentServer server = StartAgent(share, security: null);
        var tap = new WireTap(new TcpStreamConnector("127.0.0.1", server.Port));
        await using (var client = new AgentClient(new PlainTapFactory(tap, Transport), "src", Transport))
            await DownloadSecretAsync(client, target);

        Assert.IsTrue(Contains(tap.Captured, Secret));
        Assert.IsTrue(Contains(tap.Captured, SecretName));
    }

    [TestMethod]
    public async Task ManyConnections_ShareOneHandshake()
    {
        using var share = new TempFolder("sec");
        for (int i = 0; i < 16; i++)
            share.WriteFile($"f{i}.txt", [(byte)i]);
        using var peers = new SecurePeers();
        await using AgentServer server = StartAgent(share, peers.Agent());
        using SecureConnectionFactory factory = SecureFactory(server.Port, peers);
        await using var client = new AgentClient(factory, "src", Transport);

        await client.StatAsync("f0.txt", CancellationToken.None);
        // With the first connection busy the pool opens the others.
        for (int round = 0; round < 20 && client.Pool.ConnectCount < 2; round++)
            await Task.WhenAll(Enumerable.Range(0, 64).Select(i => client.StatAsync($"f{i % 16}.txt", CancellationToken.None)));

        Assert.IsGreaterThan(1L, client.Pool.ConnectCount, "The pool should have opened parallel connections.");
        Assert.AreEqual(1, factory.HandshakeCount, "Later connections must join the session instead of handshaking.");
    }

    [TestMethod]
    public async Task UnpairedMachine_CannotOpenASession()
    {
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        peers.AgentTrust.Revoke(peers.BrokerKey.Fingerprint);
        await using AgentServer server = StartAgent(share, peers.Agent());
        using SecureConnectionFactory factory = SecureFactory(server.Port, peers);
        await using var client = new AgentClient(factory, "src", Transport);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("a.txt", CancellationToken.None));
        Assert.AreEqual(NtStatus.AccessDenied, e.Status, e.ToString());
    }

    [TestMethod]
    public async Task AgentWithAnUnexpectedKey_IsRejected()
    {
        // Machine A paired with B's key, but something else answers at that address (key swap, man in the middle).
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        using var impostorKey = IdentityKey.Generate();
        var impostorTrust = new PairedMachineStore();
        impostorTrust.Add(peers.BrokerKey.PublicKey, "machine-a");
        await using AgentServer impostor = StartAgent(share, new AgentSecurity { Identity = impostorKey, PairedMachines = impostorTrust });
        using SecureConnectionFactory factory = SecureFactory(impostor.Port, peers);
        await using var client = new AgentClient(factory, "src", Transport);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("a.txt", CancellationToken.None));
        Assert.AreEqual(NtStatus.AccessDenied, e.Status, e.ToString());
    }

    [TestMethod]
    public async Task PlainClient_GetsNothingFromASecureAgent()
    {
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        await using AgentServer server = StartAgent(share, peers.Agent());
        await using var client = new AgentClient(new TcpConnectionFactory("127.0.0.1", server.Port, Transport), "src", Transport);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("a.txt", CancellationToken.None));
        Assert.IsTrue(e.IsConnectionFailure, e.ToString());
    }

    [TestMethod]
    public async Task RevokedOnAgent_DropsConnectionsAndRefusesNewOnes()
    {
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        await using AgentServer server = StartAgent(share, peers.Agent());
        using SecureConnectionFactory factory = SecureFactory(server.Port, peers);
        await using var client = new AgentClient(factory, "src", Transport);
        Assert.AreEqual(1L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);

        Assert.IsTrue(peers.AgentTrust.Revoke(peers.BrokerKey.Fingerprint));

        // The open connection is closed by the Agent; the next one is refused at the handshake (or the join).
        ProtocolStatusException? last = null;
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await client.StatAsync("a.txt", CancellationToken.None);
                Assert.Fail("A revoked machine was served.");
            }
            catch (ProtocolStatusException e)
            {
                last = e;
            }
        }
        Assert.AreEqual(NtStatus.AccessDenied, last!.Status, last.ToString());
        await WaitUntilAsync(() => server.ActiveSessionCount == 0);
    }

    [TestMethod]
    public async Task RevokedOnBroker_RefusesToConnect()
    {
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        await using AgentServer server = StartAgent(share, peers.Agent());
        using SecureConnectionFactory factory = SecureFactory(server.Port, peers);
        peers.BrokerTrust.Revoke(peers.AgentKey.Fingerprint);
        await using var client = new AgentClient(factory, "src", Transport);

        var e = await Assert.ThrowsExactlyAsync<ProtocolStatusException>(() => client.StatAsync("a.txt", CancellationToken.None));
        Assert.AreEqual(NtStatus.AccessDenied, e.Status, e.ToString());
    }

    [TestMethod]
    public async Task AgentRestart_ClientHandshakesAgain()
    {
        using var share = new TempFolder("sec");
        share.WriteFile("a.txt", [1]);
        using var peers = new SecurePeers();
        AgentServer server = StartAgent(share, peers.Agent());
        int port = server.Port;
        using SecureConnectionFactory factory = SecureFactory(port, peers);
        await using var client = new AgentClient(factory, "src", Transport with { MaxRetries = 2 });
        Assert.AreEqual(1L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);
        await server.DisposeAsync();

        await using AgentServer restarted = StartAgent(share, peers.Agent(), port);
        Assert.AreEqual(1L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);
        Assert.AreEqual(2, factory.HandshakeCount);
    }

    [TestMethod]
    public void PlainTransport_IsOffByDefault()
    {
        using var share = new TempFolder("sec");
        Assert.ThrowsExactly<ArgumentException>(() => new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => BrokerHost.CreateConnectionFactory(new BrokerOptions
        {
            SessionId = "x",
            AgentHost = "127.0.0.1",
            ShareName = "src",
            Mappings = [],
        }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition not reached in time.");
            await Task.Delay(20);
        }
    }

    /// <summary>Plain frames over a tapped stream (the development transport, captured like the secure one).</summary>
    private sealed class PlainTapFactory(WireTap tap, TransportOptions options) : IConnectionFactory
    {
        public string Description => tap.Description;

        public async ValueTask<IFrameChannel> ConnectAsync(CancellationToken cancellationToken)
            => new StreamFrameChannel(await tap.ConnectAsync(cancellationToken), ownsStream: true, options.MaxFrameSize);
    }
}
