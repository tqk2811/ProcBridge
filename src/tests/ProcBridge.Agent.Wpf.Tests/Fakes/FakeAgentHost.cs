using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Models;

namespace ProcBridge.Agent.Wpf.Tests.Fakes;

internal sealed class FakeAgentHost : IAgentHost
{
    public List<AgentConnectionInfo> Connections { get; } = [];

    public int StartCalls { get; private set; }

    public IReadOnlyList<ShareSettings>? StartedShares { get; private set; }

    public bool IsRunning { get; private set; }

    public int Port { get; private set; }

    public string Fingerprint => "BBBB-BBBB-BBBB-BBBB-BBBB-BBBB-BBBB-BBBB";

    public bool AllowPairing { get; set; }

    public event Action<IPairingPrompt>? PairingRequested;

    public void RaisePairing(IPairingPrompt prompt) => PairingRequested?.Invoke(prompt);

    public void Start(IReadOnlyList<ShareSettings> shares, IPAddress bindAddress, int port)
    {
        StartCalls++;
        StartedShares = shares;
        Port = port;
        IsRunning = true;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        return Task.CompletedTask;
    }

    public IReadOnlyList<AgentConnectionInfo> GetConnections() => Connections.ToList();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
