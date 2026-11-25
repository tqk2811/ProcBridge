using System.Net.WebSockets;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;
using ProcBridge.Transport.Secure.Interfaces;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// Machine A side of the relay tunnel (plan 8.2): every <see cref="ConnectAsync"/> is one lane of a tunnel session to
/// the share, so each connection of a pool is its own lane. Sessions are opened through <c>POST api/tunnel/sessions</c>
/// with the device token and reused while they have a free lane (0..15); a full or dead session makes a new one.
/// Put it under <c>SecureConnectionFactory</c>: the Noise handshake then runs inside the lane and the relay only sees
/// ciphertext. Thread-safe.
/// </summary>
public sealed class RelayStreamConnector : IStreamConnector, IDisposable
{
    private readonly RelayEndpoint _relay;
    private readonly Guid _shareId;
    private readonly IRelaySocketFactory _sockets;
    private readonly IRelayApi _api;
    private readonly HttpClient? _ownedHttp;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly List<Session> _sessions = [];
    private readonly SemaphoreSlim _openLock = new(1, 1);

    /// <param name="sockets">Null for <see cref="ClientWebSocketFactory"/>.</param>
    /// <param name="http">HTTP client for the REST call, not owned; null creates one on <see cref="RelayEndpoint.BaseUrl"/>.</param>
    public RelayStreamConnector(RelayEndpoint relay, Guid shareId, IRelaySocketFactory? sockets = null, HttpClient? http = null, Func<DateTime>? utcNow = null)
    {
        _relay = relay;
        _shareId = shareId;
        _sockets = sockets ?? new ClientWebSocketFactory();
        if (http is null)
        {
            _ownedHttp = new HttpClient { BaseAddress = relay.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
            http = _ownedHttp;
        }
        var api = new RelayApiImplement(http);
        api.SetBearerToken(relay.DeviceToken);
        _api = api;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Description => $"relay {_relay.BaseUrl.Host} share {_shareId}";

    /// <summary>Tunnel sessions opened so far (tests: lanes of one pool share a session).</summary>
    public int SessionCount { get; private set; }

    public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        // A lane refused by the relay (session expired meanwhile, relay restarted) is retried once on a new session.
        for (int attempt = 0; ; attempt++)
        {
            (Session session, int lane) = await AcquireLaneAsync(cancellationToken).ConfigureAwait(false);
            // The ticket goes in the Authorization header, not the URL: URLs end up in proxy and access logs.
            Uri uri = _relay.WebSocketUri($"{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={session.Info.SessionId}&{TunnelProtocol.LaneQuery}={lane}");
            WebSocket socket;
            try
            {
                socket = await _sockets.ConnectAsync(uri, session.Info.Ticket, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or InvalidOperationException or IOException)
            {
                Release(session, lane);
                Forget(session);
                if (attempt == 0)
                    continue;
                throw new IOException($"Relay refused lane {lane}: {e.Message}", e);
            }
            return new WebSocketLaneStream(socket, () => Release(session, lane));
        }
    }

    private async Task<(Session Session, int Lane)> AcquireLaneAsync(CancellationToken cancellationToken)
    {
        if (TryTakeLane() is { } taken)
            return taken;
        await _openLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have opened a session while this one waited.
            if (TryTakeLane() is { } again)
                return again;
            TunnelSessionVM info = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
            var session = new Session(info);
            lock (_lock)
            {
                _sessions.Add(session);
                SessionCount++;
                session.Busy[0] = true;
            }
            return (session, 0);
        }
        finally
        {
            _openLock.Release();
        }
    }

    private async Task<TunnelSessionVM> OpenSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _api.Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = _shareId }, cancellationToken).ConfigureAwait(false);
        }
        catch (RelayApiException e) when (e.StatusCode is 401 or 403)
        {
            throw new ProtocolStatusException(NtStatus.AccessDenied, $"Relay refused the session: {e.Code} {e.Message}", inner: e);
        }
        catch (RelayApiException e)
        {
            // 409 DEVICE_OFFLINE and friends: the Agent may come back, so this is a connection failure.
            throw new IOException($"Relay could not open a session: {e.Code} {e.Message}", e);
        }
        catch (HttpRequestException e)
        {
            throw new IOException($"Relay unreachable: {e.Message}", e);
        }
    }

    /// <summary>
    /// The lowest free lane of a usable session. A session past its deadline still takes lanes while one of its lanes
    /// is open (the relay keeps it alive then).
    /// </summary>
    private (Session, int)? TryTakeLane()
    {
        DateTime now = _utcNow();
        lock (_lock)
        {
            _sessions.RemoveAll(x => x.OpenLanes == 0 && x.Info.ExpiresAt <= now);
            foreach (Session session in _sessions)
            {
                if (session.OpenLanes == 0 && session.Info.ExpiresAt <= now)
                    continue;
                for (int lane = 0; lane < TunnelProtocol.MaxLanes; lane++)
                {
                    if (!session.Busy[lane])
                    {
                        session.Busy[lane] = true;
                        return (session, lane);
                    }
                }
            }
            return null;
        }
    }

    private void Release(Session session, int lane)
    {
        lock (_lock)
            session.Busy[lane] = false;
    }

    private void Forget(Session session)
    {
        lock (_lock)
            _sessions.Remove(session);
    }

    public void Dispose()
    {
        _ownedHttp?.Dispose();
        _openLock.Dispose();
    }

    private sealed class Session(TunnelSessionVM info)
    {
        public TunnelSessionVM Info { get; } = info;

        public bool[] Busy { get; } = new bool[TunnelProtocol.MaxLanes];

        public int OpenLanes => Busy.Count(x => x);
    }
}
