using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Crypto.Identity;
using ProcBridge.Protocol.Models;
using ProcBridge.Relay.Services.Tunnel;
using ProcBridge.Tests.Lan;
using ProcBridge.Transport.Relay;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Interfaces;
using ProcBridge.Transport.Secure.Models;
using Microsoft.Extensions.DependencyInjection;

namespace ProcBridge.Tests.Relay;

/// <summary>
/// Pairing when machine A only reaches machine B through the relay: B opens a pairing slot under its invite code, A finds
/// it by that code, then the usual Noise XX + 6-digit code pairing runs end to end inside a lane, and the new pair opens
/// an encrypted session through the relay by HostId.
/// </summary>
[TestClass]
public sealed class RelayPairingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TransportOptions Transport = new() { ConnectTimeout = TimeSpan.FromSeconds(10), RequestTimeout = TimeSpan.FromSeconds(10), MaxRetries = 0 };

    private sealed class Machines : IAsyncDisposable
    {
        private Machines(InProcessRelay relay, TempFolder share, bool allowPairing)
        {
            Relay = relay;
            Server = new AgentServer([new AgentShare("src", share.Path)],
                new AgentOptions
                {
                    BindAddress = IPAddress.Loopback,
                    Port = 0,
                    PairingTimeout = TimeSpan.FromSeconds(20),
                    Relay = new AgentRelayOptions { Endpoint = relay.Endpoint, Sockets = relay.Sockets },
                },
                new AgentSecurity { Identity = AgentKey, PairedMachines = AgentTrust, AllowPairing = allowPairing });
            Client = new PairingClient(BrokerKey, BrokerTrust, "machine-a");
        }

        public InProcessRelay Relay { get; }
        public IdentityKey AgentKey { get; } = IdentityKey.Generate();
        public IdentityKey BrokerKey { get; } = IdentityKey.Generate();
        public PairedMachineStore AgentTrust { get; } = new();
        public PairedMachineStore BrokerTrust { get; } = new();
        public AgentServer Server { get; }
        public PairingClient Client { get; }

        public static async Task<Machines> StartAsync(InProcessRelay relay, TempFolder share, bool allowPairing = true)
        {
            var machines = new Machines(relay, share, allowPairing);
            machines.Server.Start();
            await relay.WaitOnlineAsync(machines.AgentKey.Fingerprint);
            return machines;
        }

        public RelayPairingConnector Connector(string code) => new(Relay.Endpoint, code, Relay.Sockets, Relay.Factory.CreateClient());

        /// <summary>Waits until the Agent shows an invite code different from <paramref name="previous"/>.</summary>
        public async Task<string> WaitCodeAsync(string? previous = null)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                if (Server.RelayPairingCode is { } code && code != previous)
                    return code;
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("The Agent did not get a relay pairing code.");
                await Task.Delay(20);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            AgentKey.Dispose();
            BrokerKey.Dispose();
        }
    }

    [TestMethod]
    public async Task Pairing_ThroughTheRelay_ByInviteCode_ThenSessionByHostId()
    {
        await using var relay = InProcessRelay.Start();
        using var share = new TempFolder("relay-pair");
        share.WriteFile("a.txt", [1, 2, 3]);
        await using var m = await Machines.StartAsync(relay, share);
        string invite = await m.WaitCodeAsync();
        Assert.MatchesRegex("^[0-9]{4}-[0-9]{4}$", invite);

        var requested = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Server.PairingRequested += request =>
        {
            requested.TrySetResult(request);
            request.Accept();
        };
        string? shownAgentFingerprint = null;
        using RelayPairingConnector connector = m.Connector(invite);
        PairedMachine agent = await m.Client.PairAsync(connector, async (fingerprint, _) =>
        {
            shownAgentFingerprint = fingerprint;
            return (await requested.Task).Code;
        }, CancellationToken.None);

        PairingRequest seen = await requested.Task;
        Assert.IsTrue(await seen.Completion.WaitAsync(Timeout));
        Assert.AreEqual(m.BrokerKey.Fingerprint, seen.PeerFingerprint, "B shows A's real fingerprint.");
        Assert.AreEqual(m.AgentKey.Fingerprint, shownAgentFingerprint, "A shows B's real fingerprint.");
        Assert.AreEqual(m.AgentKey.Fingerprint, connector.ExpectedPeerFingerprint, "The relay named B's HostId.");
        Assert.AreEqual(m.AgentKey.Fingerprint, agent.Fingerprint);
        Assert.AreEqual(relay.Endpoint.BaseUrl, RelayPairingConnector.TryParseRelayAddress(agent.Address));
        Assert.IsTrue(m.AgentTrust.IsTrusted(m.BrokerKey.PublicKey));
        Assert.IsTrue(m.BrokerTrust.IsTrusted(m.AgentKey.PublicKey));

        // The slot was single use: B opens a new one with a new code, and the old code finds nothing.
        string next = await m.WaitCodeAsync(invite);
        Assert.AreNotEqual(invite, next);
        using RelayPairingConnector stale = m.Connector(invite);
        await Assert.ThrowsExactlyAsync<IOException>(() => m.Client.PairAsync(stale, (_, _) => Task.FromResult("000000"), CancellationToken.None));

        // Paired: the normal session through the relay by HostId (no account anywhere).
        using var lanes = new RelayStreamConnector(relay.Endpoint, agent.Fingerprint, relay.Sockets, relay.Factory.CreateClient());
        using var factory = new SecureConnectionFactory(lanes, m.BrokerKey, m.BrokerTrust, agent.PublicKey, Transport);
        await using var client = new AgentClient(factory, "src", Transport);
        Assert.AreEqual(3L, (await client.StatAsync("a.txt", CancellationToken.None)).Size);
    }

    [TestMethod]
    public async Task PairingOff_ShowsNoCode_AndOpensNoSlot()
    {
        await using var relay = InProcessRelay.Start();
        using var share = new TempFolder("relay-pair");
        await using var m = await Machines.StartAsync(relay, share, allowPairing: false);
        TunnelHub hub = relay.Factory.Services.GetRequiredService<TunnelHub>();
        await Task.Delay(200);
        Assert.IsNull(m.Server.RelayPairingCode);
        Assert.IsFalse(hub.HasPairingSlot(m.AgentKey.Fingerprint));

        m.Server.AllowPairing = true;
        await m.WaitCodeAsync();
        Assert.IsTrue(hub.HasPairingSlot(m.AgentKey.Fingerprint));

        m.Server.AllowPairing = false;
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (hub.HasPairingSlot(m.AgentKey.Fingerprint) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsFalse(hub.HasPairingSlot(m.AgentKey.Fingerprint), "Turning pairing off closes the slot on the relay.");
        Assert.IsNull(m.Server.RelayPairingCode);
    }

    [TestMethod]
    public async Task Pairing_WhenTheHandshakeProvesAnotherKey_IsRefused()
    {
        // A relay that routes the lookup to some other machine: its key does not match the HostId it named.
        using var share = new TempFolder("relay-pair");
        using var agentKey = IdentityKey.Generate();
        using var brokerKey = IdentityKey.Generate();
        var agentTrust = new PairedMachineStore();
        var brokerTrust = new PairedMachineStore();
        await using var server = new AgentServer([new AgentShare("src", share.Path)],
            new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0, PairingTimeout = TimeSpan.FromSeconds(20) },
            new AgentSecurity { Identity = agentKey, PairedMachines = agentTrust, AllowPairing = true });
        server.Start();
        bool asked = false;
        server.PairingRequested += request => request.Accept();
        using var other = IdentityKey.Generate();
        var connector = new HintedConnector(new TcpStreamConnector("127.0.0.1", server.Port), other.Fingerprint);

        await Assert.ThrowsExactlyAsync<PairingFailedException>(() => new PairingClient(brokerKey, brokerTrust, "machine-a").PairAsync(connector, (_, _) =>
        {
            asked = true;
            return Task.FromResult("000000");
        }, CancellationToken.None));
        Assert.IsFalse(asked, "A must not even ask for the code.");
        Assert.IsEmpty(brokerTrust.List());
        Assert.IsEmpty(agentTrust.List());
    }

    private sealed class HintedConnector(IStreamConnector inner, string expected) : IPeerHintConnector
    {
        public string Description => inner.Description;

        public string? ExpectedPeerFingerprint => expected;

        public ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);
    }
}
