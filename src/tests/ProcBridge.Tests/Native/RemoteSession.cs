using System.Net;
using ProcBridge.Agent;
using ProcBridge.Agent.Models;
using ProcBridge.Broker;
using ProcBridge.Broker.Models;
using ProcBridge.HookIpc;
using ProcBridge.Tests.Native.Models;

namespace ProcBridge.Tests.Native;

/// <summary>
/// M2 set-up on one machine: an Agent on loopback shares the sandbox's real folder, and a Broker in this test
/// process maps the sandbox's virtual folder onto that share. Processes started through <see cref="Run"/> get the
/// Broker session (ProcBridge.Spawn --broker), so every file they see in the virtual folder comes over TCP.
/// Does not own the sandbox; owns the Agent and the Broker.
/// </summary>
internal sealed class RemoteSession : IAsyncDisposable
{
    private readonly HookSandbox _sandbox;
    private readonly AgentServer _agent;
    private readonly BrokerHost _broker;

    private RemoteSession(HookSandbox sandbox, AgentServer agent, BrokerHost broker, string sessionId)
    {
        _sandbox = sandbox;
        _agent = agent;
        _broker = broker;
        SessionId = sessionId;
    }

    public string SessionId { get; }

    /// <summary>The Broker's cache folder (inside the sandbox, so on the same volume as the real folder).</summary>
    public string CacheRoot => _broker.Cache.CacheRoot;

    /// <summary>The Broker of this session (write-back queue, conflicts).</summary>
    public BrokerHost Broker => _broker;

    /// <param name="writable">Serve the real folder read/write (M3) instead of read only.</param>
    public static RemoteSession Start(HookSandbox sandbox, bool writable = false, bool writeThrough = false)
    {
        var agent = new AgentServer([new AgentShare("share", sandbox.RealRoot, writable)], new AgentOptions { BindAddress = IPAddress.Loopback, Port = 0 });
        agent.Start();
        string sessionId = HookIpcProtocol.NewSessionId();
        BrokerHost broker = BrokerHost.Start(new BrokerOptions
        {
            SessionId = sessionId,
            AgentHost = "127.0.0.1",
            AgentPort = agent.Port,
            ShareName = "share",
            Mappings = [new VirtualMapping(sandbox.VirtualRoot, "")],
            CacheRoot = Path.Combine(sandbox.BaseDirectory, "cache"),
            WriteQueueRoot = Path.Combine(sandbox.BaseDirectory, "queue"),
            WriteThrough = writeThrough,
        });
        return new RemoteSession(sandbox, agent, broker, sessionId);
    }

    /// <summary>Runs a command line under ProcBridge.Spawn with this Broker session.</summary>
    public ProcessResult Run(string commandLine, string? workingDirectory = null)
        => RunWithBrokerProcess(Environment.ProcessId, commandLine, workingDirectory);

    /// <summary>Like <see cref="Run"/>, with extra environment variables for the tree.</summary>
    public ProcessResult Run(string commandLine, IReadOnlyDictionary<string, string> environment)
        => _sandbox.RunSpawn($"--broker {SessionId} {Environment.ProcessId} -- {commandLine}", environment);

    /// <summary>Like <see cref="Run"/>, claiming that another process owns the pipe (the hook must refuse it).</summary>
    public ProcessResult RunWithBrokerProcess(int brokerProcessId, string commandLine, string? workingDirectory = null)
        => _sandbox.RunSpawn($"--broker {SessionId} {brokerProcessId} -- {commandLine}", workingDirectory);

    public async ValueTask DisposeAsync()
    {
        await _broker.DisposeAsync();
        await _agent.DisposeAsync();
    }
}
