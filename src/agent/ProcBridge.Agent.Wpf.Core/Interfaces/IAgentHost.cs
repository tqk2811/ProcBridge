using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Agent.Wpf.Core.Interfaces;

/// <summary>The Agent server hosted by the app: start, stop, pairing and the open connections.</summary>
public interface IAgentHost : IAsyncDisposable
{
    bool IsRunning { get; }

    /// <summary>Port listened on while running.</summary>
    int Port { get; }

    /// <summary>Fingerprint of this machine's identity key (known before the server starts).</summary>
    string Fingerprint { get; }

    /// <summary>Accept pairing requests; kept across restarts of the server.</summary>
    bool AllowPairing { get; set; }

    /// <summary>Raised on a background thread when a machine A asks to pair while <see cref="AllowPairing"/> is on.</summary>
    event Action<IPairingPrompt>? PairingRequested;

    /// <summary>State of the relay link while running: null without relay, else a short text for the status bar.</summary>
    string? RelayState { get; }

    /// <summary>
    /// Invite code machines A type to pair through the relay (<c>1234-5678</c>) while running with a relay and
    /// <see cref="AllowPairing"/> on; null otherwise or until the relay opened the slot. It changes after each use and
    /// expiry: <see cref="RelayStatusChanged"/> says when.
    /// </summary>
    string? RelayPairingCode { get; }

    /// <summary>Raised on a background thread when <see cref="RelayPairingCode"/> or <see cref="RelayState"/> may have changed.</summary>
    event Action? RelayStatusChanged;

    /// <param name="relay">Also serve through this relay (plan 8.2); null for the LAN only.</param>
    /// <exception cref="InvalidOperationException">Already running.</exception>
    /// <exception cref="DirectoryNotFoundException">A share folder does not exist.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The port cannot be listened on.</exception>
    void Start(IReadOnlyList<ShareSettings> shares, IPAddress bindAddress, int port, RelayEndpoint? relay = null);

    Task StopAsync();

    IReadOnlyList<AgentConnectionInfo> GetConnections();
}
