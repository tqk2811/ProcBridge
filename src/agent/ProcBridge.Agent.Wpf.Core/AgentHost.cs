using System.Net;
using ProcBridge.Agent.Models;
using ProcBridge.Agent.Wpf.Core.Interfaces;
using ProcBridge.Agent.Wpf.Core.Models;
using ProcBridge.Crypto.Identity;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.Transport.Secure;

namespace ProcBridge.Agent.Wpf.Core;

/// <summary>
/// Runs an <see cref="AgentServer"/> inside the app, always encrypted. The identity key and both stores are borrowed
/// (the app owns them); the server is created on <see cref="Start"/> and disposed on <see cref="StopAsync"/>.
/// Called from the UI thread only.
/// </summary>
public sealed class AgentHost : IAgentHost
{
    private readonly IdentityKey _identity;
    private readonly PairedMachineStore _pairedMachines;
    private readonly ExplorerPolicyStore _explorerPolicies;
    private AgentServer? _server;
    private bool _allowPairing;

    public AgentHost(IdentityKey identity, PairedMachineStore pairedMachines, ExplorerPolicyStore explorerPolicies)
    {
        _identity = identity;
        _pairedMachines = pairedMachines;
        _explorerPolicies = explorerPolicies;
        Fingerprint = KeyFingerprint.Compute(identity.PublicKey);
    }

    public event Action<IPairingPrompt>? PairingRequested;

    public event Action? RelayStatusChanged;

    public bool IsRunning => _server is not null;

    public int Port => _server?.Port ?? 0;

    public string Fingerprint { get; }

    public bool AllowPairing
    {
        get => _allowPairing;
        set
        {
            _allowPairing = value;
            if (_server is not null)
                _server.AllowPairing = value;
        }
    }

    public string? RelayState => _server?.Relay switch
    {
        null => null,
        { IsConnected: true, PairingError: { } pairingError } => $"relay: đã kết nối, chưa có mã ghép cặp ({pairingError})",
        { IsConnected: true } => "relay: đã kết nối",
        { LastError: { } error } => $"relay: mất kết nối ({error}), đang thử lại",
        _ => "relay: đang kết nối",
    };

    public string? RelayPairingCode => _server?.RelayPairingCode;

    public void Start(IReadOnlyList<ShareSettings> shares, IPAddress bindAddress, int port, RelayEndpoint? relay = null)
    {
        if (_server is not null)
            throw new InvalidOperationException("The Agent is already running.");
        // "Cho ghi" (AllowWrite) makes the share writable, like --write-share of the console Agent.
        var agentShares = shares.Select(x => new AgentShare(x.Name, x.Path, x.AllowWrite)).ToList();
        var security = new AgentSecurity
        {
            Identity = _identity,
            PairedMachines = _pairedMachines,
            ExplorerPolicies = _explorerPolicies,
            AllowPairing = _allowPairing,
        };
        var server = new AgentServer(agentShares, new AgentOptions
        {
            BindAddress = bindAddress,
            Port = port,
            Relay = relay is null ? null : new AgentRelayOptions { Endpoint = relay },
        }, security);
        server.PairingRequested += OnPairingRequested;
        if (server.Relay is { } link)
        {
            link.PairingCodeChanged += OnRelayStatusChanged;
            link.ConnectionChanged += OnRelayConnectionChanged;
        }
        try
        {
            server.Start();
        }
        catch
        {
            server.PairingRequested -= OnPairingRequested;
            UnsubscribeRelay(server);
            server.DisposeAsync().AsTask().GetAwaiter().GetResult(); // Nothing accepted yet, so this completes at once.
            throw;
        }
        _server = server;
        OnRelayStatusChanged();
    }

    public async Task StopAsync()
    {
        AgentServer? server = _server;
        if (server is null)
            return;
        _server = null;
        server.PairingRequested -= OnPairingRequested;
        UnsubscribeRelay(server);
        await server.DisposeAsync();
        OnRelayStatusChanged();
    }

    private void UnsubscribeRelay(AgentServer server)
    {
        if (server.Relay is { } link)
        {
            link.PairingCodeChanged -= OnRelayStatusChanged;
            link.ConnectionChanged -= OnRelayConnectionChanged;
        }
    }

    private void OnRelayConnectionChanged(bool connected) => OnRelayStatusChanged();

    private void OnRelayStatusChanged() => RelayStatusChanged?.Invoke();

    public IReadOnlyList<AgentConnectionInfo> GetConnections() => _server?.GetConnections() ?? [];

    private void OnPairingRequested(PairingRequest request)
    {
        Action<IPairingPrompt>? handler = PairingRequested;
        if (handler is null)
            request.Reject();
        else
            handler(new PairingRequestPrompt(request));
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
