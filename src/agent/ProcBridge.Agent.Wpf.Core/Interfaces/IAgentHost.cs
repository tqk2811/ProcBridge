using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Models;

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

    /// <exception cref="InvalidOperationException">Already running.</exception>
    /// <exception cref="DirectoryNotFoundException">A share folder does not exist.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">The port cannot be listened on.</exception>
    void Start(IReadOnlyList<ShareSettings> shares, IPAddress bindAddress, int port);

    Task StopAsync();

    IReadOnlyList<AgentConnectionInfo> GetConnections();
}
