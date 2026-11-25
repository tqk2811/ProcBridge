using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol.Models;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Tests.Secure;

/// <summary>First-time pairing over TCP: XX handshake, short code, decision on machine B, then a normal IK session.</summary>
[TestClass]
public sealed class PairingTransportTests
{
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10), MaxRetries = 0 };

    private sealed class Machines : IAsyncDisposable
    {
        public Machines(TempFolder share, bool allowPairing = true)
        {
            Server = new AgentServer([new ReadOnlyShare("src", share.Path)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, PairingTimeout = TimeSpan.FromSeconds(20) },
                new AgentSecurity { Identity = AgentKey, PairedMachines = AgentTrust, AllowPairing = allowPairing });
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

    [TestMethod]
    public async Task Pairing_WithTheRightCode_StoresBothKeys_AndOpensASession()
    {
        using var share = new TempFolder("pair");
        share.WriteFile("a.txt", [1, 2]);
        await using var m = new Machines(share);
        var requested = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request =>
        {
            requested.TrySetResult(request);
            request.Accept();
        };

        string? shownAgentFingerprint = null;
        PairedMachine agent = await m.Client.PairAsync(m.Connector, async (fingerprint, _) =>
        {
            shownAgentFingerprint = fingerprint;
            return (await requested.Task).Code;
        }, CancellationToken.None);

        PairingRequest seen = await requested.Task;
        Assert.IsTrue(await seen.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(m.BrokerKey.Fingerprint, seen.PeerFingerprint, "B must show A's real fingerprint.");
        Assert.AreEqual("machine-a", seen.PeerName);
        Assert.MatchesRegex("^[0-9]{6}$", seen.Code);
        Assert.AreEqual(m.AgentKey.Fingerprint, shownAgentFingerprint, "A must show B's real fingerprint.");
        Assert.AreEqual(m.AgentKey.Fingerprint, agent.Fingerprint);
        Assert.AreEqual($"127.0.0.1:{m.Server.Port}", agent.Address);
        Assert.IsTrue(m.AgentTrust.IsTrusted(m.BrokerKey.PublicKey));
        Assert.IsTrue(m.BrokerTrust.IsTrusted(m.AgentKey.PublicKey));

        using var factory = new SecureConnectionFactory(m.Connector, m.BrokerKey, m.BrokerTrust, agent.PublicKey, Transport);
        await using var client = new AgentClient(factory, "src", Transport);
        Assert.AreEqual(2L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);
    }

    [TestMethod]
    public async Task Pairing_WithAWrongCode_IsRejected()
    {
        using var share = new TempFolder("pair");
        await using var m = new Machines(share);
        var requested = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request =>
        {
            requested.TrySetResult(request);
            // Even an early approval must not trust a machine that typed the wrong code.
            request.Accept();
        };

        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => m.Client.PairAsync(m.Connector, async (_, _) =>
        {
            string code = (await requested.Task).Code;
            return code == "000000" ? "111111" : "000000";
        }, CancellationToken.None));

        Assert.IsFalse(await (await requested.Task).Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsEmpty(m.AgentTrust.List());
        Assert.IsEmpty(m.BrokerTrust.List());
    }

    [TestMethod]
    public async Task Pairing_RejectedOnTheAgent_StoresNothing()
    {
        using var share = new TempFolder("pair");
        await using var m = new Machines(share);
        var requested = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request => requested.TrySetResult(request);

        Task<PairedMachine> pairing = m.Client.PairAsync(m.Connector, async (_, _) =>
        {
            PairingRequest request = await requested.Task;
            request.Reject();
            return request.Code;
        }, CancellationToken.None);

        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => pairing);
        Assert.IsFalse(await (await requested.Task).Completion);
        Assert.IsEmpty(m.AgentTrust.List());
        Assert.IsEmpty(m.BrokerTrust.List());
    }

    [TestMethod]
    public async Task Pairing_WhenNotAllowed_IsRefused()
    {
        using var share = new TempFolder("pair");
        await using var m = new Machines(share, allowPairing: false);
        bool raised = false;
        m.Server.PairingRequested += _ => raised = true;

        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => m.Client.PairAsync(m.Connector, (_, _) => Task.FromResult("123456"), CancellationToken.None));
        Assert.IsFalse(raised);
        Assert.IsEmpty(m.AgentTrust.List());
    }

    [TestMethod]
    public void PairedMachineStore_PersistsAndRevokes()
    {
        using var folder = new TempFolder("pair-store");
        string path = folder.Combine("paired.bin");
        var protector = new DpapiKeyProtector();
        using var key = IdentityKey.Generate();
        var store = new PairedMachineStore(path, protector);
        store.Add(key.PublicKey, "machine-b", "host:47800");

        var reloaded = new PairedMachineStore(path, protector);
        PairedMachine machine = reloaded.List().Single();
        Assert.AreEqual(key.Fingerprint, machine.Fingerprint);
        Assert.AreEqual("machine-b", machine.Name);
        Assert.AreEqual(machine, reloaded.FindByAddress("HOST:47800"));
        Assert.IsTrue(reloaded.IsTrusted(key.PublicKey));

        string? revoked = null;
        reloaded.Revoked += x => revoked = x.Fingerprint;
        Assert.IsTrue(reloaded.Revoke(key.Fingerprint));
        Assert.AreEqual(key.Fingerprint, revoked);
        Assert.IsFalse(new PairedMachineStore(path, protector).IsTrusted(key.PublicKey));
        Assert.IsFalse(reloaded.Revoke(key.Fingerprint));
    }
}
