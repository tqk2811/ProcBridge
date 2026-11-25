using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ProcBridge.Agent.Explorer;
using ProcBridge.Agent.Models;
using ProcBridge.Protocol.Interfaces;
using ProcBridge.Protocol.Transport;
using ProcBridge.Transport.Secure;
using ProcBridge.Transport.Secure.Models;

namespace ProcBridge.Agent;

/// <summary>
/// TCP listener of the Agent: accepts connections from Brokers and runs one <see cref="AgentSession"/> per
/// connection. With an <see cref="AgentSecurity"/> every connection first goes through the <see cref="SecureServer"/>
/// (Noise IK handshake against the paired machines, or a pairing) and frames are encrypted end to end (plan section 9).
/// Without it the server only starts when <see cref="AgentOptions.AllowPlaintextTransport"/> is set (development).
/// </summary>
public sealed class AgentServer : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, AgentShare> _shares;
    private readonly AgentOptions _options;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<TcpClient, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, Lazy<ShareWatcher>> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecureServer? _secure;
    private readonly AgentSecurity? _security;
    private Task _acceptLoop = Task.CompletedTask;
    private Task _refreshLoop = Task.CompletedTask;
    private int _disposed;

    /// <param name="security">Identity and paired machines; null serves plain frames and needs <see cref="AgentOptions.AllowPlaintextTransport"/>.</param>
    /// <exception cref="ArgumentException">No security and plain transport not allowed.</exception>
    public AgentServer(IEnumerable<AgentShare> shares, AgentOptions options, AgentSecurity? security = null)
    {
        if (security is null && !options.AllowPlaintextTransport)
            throw new ArgumentException("The Agent needs an identity and paired machines (AgentSecurity); plain transport is for development only.", nameof(security));
        _shares = shares.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _options = options;
        _listener = new TcpListener(options.BindAddress, options.Port);
        _security = security;
        if (security is not null)
        {
            _secure = new SecureServer(security.Identity, security.PairedMachines, new SecureServerOptions
            {
                HandshakeTimeout = options.HandshakeTimeout,
                MaxPendingHandshakes = options.MaxPendingHandshakes,
                MaxPendingHandshakesPerAddress = options.MaxPendingHandshakesPerAddress,
                AllowPairing = security.AllowPairing,
                PairingTimeout = options.PairingTimeout,
                MaxFrameSize = options.MaxFrameSize,
            });
            _secure.PeerRevoked += OnPeerRevoked;
            security.ExplorerPolicies.Changed += AbortPeer;
            _secure.PairingRequested += OnPairingRequested;
        }
    }

    /// <summary>
    /// A machine A asks to pair: show <see cref="PairingRequest.Code"/> and <see cref="PairingRequest.PeerFingerprint"/>,
    /// then call <see cref="PairingRequest.Accept"/> or <see cref="PairingRequest.Reject"/>. Raised only while
    /// <see cref="AllowPairing"/> is on; without a handler the request is rejected.
    /// </summary>
    public event Action<PairingRequest>? PairingRequested;

    /// <summary>True when connections are encrypted (an <see cref="AgentSecurity"/> was given).</summary>
    public bool IsSecure => _secure is not null;

    /// <summary>Accept pairing requests (the UI turns it on while pairing a new machine). Always false without security.</summary>
    public bool AllowPairing
    {
        get => _secure?.AllowPairing ?? false;
        set
        {
            if (_secure is null)
                throw new InvalidOperationException("Pairing needs an AgentSecurity.");
            _secure.AllowPairing = value;
        }
    }

    /// <summary>Fingerprint of this Agent's identity key, or null without security.</summary>
    public string? Fingerprint => _secure?.Fingerprint;

    /// <summary>Port actually listened on (useful when <see cref="AgentOptions.Port"/> is 0).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ActiveSessionCount => _connections.Count;

    /// <summary>Snapshot of the open connections (one machine A usually holds several: the pool).</summary>
    public IReadOnlyList<AgentConnectionInfo> GetConnections()
        => _connections.Values.Select(x => new AgentConnectionInfo
        {
            RemoteAddress = x.RemoteAddress,
            PeerFingerprint = x.PeerFingerprint,
            ConnectedAtUtc = x.ConnectedAtUtc,
        }).ToList();

    /// <summary>The watcher of <paramref name="share"/>, created on first use and shared by every connection.</summary>
    internal ShareWatcher GetWatcher(AgentShare share)
        => _watchers.GetOrAdd(share.Name, _ => new Lazy<ShareWatcher>(() => new ShareWatcher(share.RootPath, _options.WatchBufferSize, _options.WatchBatchDelay))).Value;

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
        if (_secure is not null && _options.TrustRefreshInterval > TimeSpan.Zero)
            _refreshLoop = Task.Run(RefreshLoopAsync);
    }

    /// <summary>
    /// Re-reads the trust files while the Agent runs, so a revocation or a narrowed profile made by another process
    /// takes effect on open connections too (the stores raise the events that close them).
    /// </summary>
    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.TrustRefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    _security!.PairedMachines.Refresh();
                    _security.ExplorerPolicies.Refresh();
                }
                catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException or InvalidDataException)
                {
                    // A file that cannot be read this time (another process holds it): the next tick tries again.
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            client.NoDelay = true;
            // Registered before it runs, so a connection that fails at once cannot be removed before it is added.
            var connection = new Connection(client);
            _connections[client] = connection;
            connection.Run = Task.Run(() => RunConnectionAsync(connection));
        }
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        try
        {
            IFrameChannel channel;
            ExplorerDispatcher? explorer = null;
            if (_secure is null)
            {
                channel = new StreamFrameChannel(connection.Client.GetStream(), ownsStream: true, _options.MaxFrameSize);
            }
            else
            {
                string remote = connection.Client.Client.RemoteEndPoint?.ToString() ?? string.Empty;
                SecureFrameChannel? secure = await _secure.AcceptAsync(connection.Client.GetStream(), remote, _stopping.Token).ConfigureAwait(false);
                if (secure is null)
                    return;
                connection.PeerFingerprint = secure.PeerFingerprint;
                // Revoked while the handshake ran: the revocation event may have missed this connection.
                if (!_secure.PairedMachines.IsTrusted(secure.PeerFingerprint))
                {
                    await secure.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                channel = secure;
                // The Explorer acts for the authenticated machine A only, within the profile machine B set for it.
                explorer = new ExplorerDispatcher(new ExplorerSession(_security!.ExplorerPolicies.Get(secure.PeerFingerprint), secure.PeerFingerprint, _security.ExplorerAudit));
            }
            var session = new AgentSession(channel, connection.Client, _shares, _options, GetWatcher, explorer);
            await session.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A broken client must not stop the server; the session already closed its socket.
        }
        finally
        {
            connection.Client.Dispose();
            _connections.TryRemove(connection.Client, out _);
        }
    }

    private void OnPairingRequested(PairingRequest request)
    {
        Action<PairingRequest>? handler = PairingRequested;
        if (handler is null)
            request.Reject();
        else
            handler(request);
    }

    /// <summary>A revoked machine loses its connections and its Explorer profile (a machine paired again starts with nothing).</summary>
    private void OnPeerRevoked(string fingerprint)
    {
        AbortPeer(fingerprint);
        _security!.ExplorerPolicies.Remove(fingerprint);
    }

    /// <summary>Closes every connection of a machine whose trust or Explorer profile changed.</summary>
    private void AbortPeer(string fingerprint)
    {
        foreach (Connection connection in _connections.Values)
        {
            if (connection.PeerFingerprint == fingerprint)
                connection.Client.Dispose();
        }
    }

    /// <summary>Stops listening and closes every open connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await _refreshLoop.ConfigureAwait(false);
        foreach (Connection connection in _connections.Values)
            connection.Client.Dispose();
        await Task.WhenAll(_connections.Values.Select(x => x.Run)).ConfigureAwait(false);
        if (_secure is not null)
        {
            _secure.PeerRevoked -= OnPeerRevoked;
            _security!.ExplorerPolicies.Changed -= AbortPeer;
            _secure.Dispose();
        }
        foreach (Lazy<ShareWatcher> watcher in _watchers.Values)
        {
            if (watcher.IsValueCreated)
                watcher.Value.Dispose();
        }
        _stopping.Dispose();
    }

    /// <summary>One accepted socket; <see cref="PeerFingerprint"/> is set once the handshake authenticated the peer.</summary>
    private sealed class Connection(TcpClient client)
    {
        private volatile string? _peerFingerprint;

        public TcpClient Client { get; } = client;

        public string RemoteAddress { get; } = client.Client.RemoteEndPoint?.ToString() ?? string.Empty;

        public DateTime ConnectedAtUtc { get; } = DateTime.UtcNow;

        public Task Run { get; set; } = Task.CompletedTask;

        public string? PeerFingerprint
        {
            get => _peerFingerprint;
            set => _peerFingerprint = value;
        }
    }
}
