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

    public event Action? RelayStatusChanged;

    /// <summary>What the relay link raises when the invite code rotates (after a use or its expiry).</summary>
    public void RaiseRelayStatusChanged() => RelayStatusChanged?.Invoke();

    public string? RelayState => StartedRelay is null || !IsRunning ? null : "relay: đã kết nối";

    /// <summary>What the real host reports while running with a relay and pairing on; tests set it.</summary>
    public string? RelayPairingCodeValue { get; set; }

    public string? RelayPairingCode => StartedRelay is null || !IsRunning || !AllowPairing ? null : RelayPairingCodeValue;

    public Transport.Relay.Models.RelayEndpoint? StartedRelay { get; private set; }

    public void Start(IReadOnlyList<ShareSettings> shares, IPAddress bindAddress, int port, Transport.Relay.Models.RelayEndpoint? relay = null)
    {
        StartCalls++;
        StartedRelay = relay;
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
