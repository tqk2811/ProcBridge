using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.HookIpc;
using ProcBridge.Tests.Native.Models;
using ProcBridge.Tests.Relay;
using ProcBridge.Tests.Secure;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M6 set-up on one machine: like <see cref="RemoteSession"/>, but the Broker reaches the Agent ONLY through an
/// in-process relay. The Agent keeps the relay control channel, proving its HostId with its identity key; the Broker
/// opens tunnel sessions to that HostId (no account anywhere); every pool connection is a relay lane with the Noise
/// handshake inside it. The Agent's TCP
/// listener is bound to loopback but the Broker is never told its port.
/// </summary>
internal sealed class RelaySession : IAsyncDisposable
{
    private readonly HookSandbox _sandbox;
    private readonly AgentServer _agent;
    private readonly BrokerHost _broker;
    private readonly SecurePeers _peers;

    private RelaySession(HookSandbox sandbox, InProcessRelay relay, AgentServer agent, BrokerHost broker, string sessionId, SecurePeers peers)
    {
        _sandbox = sandbox;
        Relay = relay;
        _agent = agent;
        _broker = broker;
        SessionId = sessionId;
        _peers = peers;
    }

    public string SessionId { get; }

    public InProcessRelay Relay { get; }

    public AgentServer Agent => _agent;

    public static async Task<RelaySession> StartAsync(HookSandbox sandbox)
    {
        InProcessRelay relay = InProcessRelay.Start();
        var peers = new SecurePeers();
        var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot)], new AgentOptions
        {
            BindAddress = IPAddress.Loopback,
            Port = 0,
            Relay = new AgentRelayOptions { Endpoint = relay.Endpoint, Sockets = relay.Sockets },
        }, peers.Agent());
        agent.Start();
        await relay.WaitOnlineAsync(KeyFingerprint.Compute(peers.Broker.AgentPublicKey));

        string sessionId = HookIpcProtocol.NewSessionId();
        BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = sessionId,
            AgentHost = "unused.invalid",
            AgentPort = 1,
            ShareName = "share",
            Mappings = [new VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            Security = peers.Broker,
            WriteQueueRoot = Path.Combine(sandbox.BaseDirectory, "queue"),
            Relay = new BrokerRelayOptions
            {
                Endpoint = relay.Endpoint,
                Sockets = relay.Sockets,
                Http = relay.Factory.CreateClient(),
            },
        });
        return new RelaySession(sandbox, relay, agent, broker, sessionId, peers);
    }

    /// <summary>Runs a command line under ProcBridge.Spawn with this Broker session.</summary>
    public ProcessResult Run(string commandLine)
        => _sandbox.RunSpawn($"--broker {SessionId} {Environment.ProcessId} -- {commandLine}");

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _agent.DisposeAsync();
        await Relay.DisposeAsync();
        _peers.Dispose();
    }
}
