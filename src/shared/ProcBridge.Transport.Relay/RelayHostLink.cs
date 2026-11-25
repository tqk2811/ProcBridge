using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// Machine B side of the relay tunnel (plan 8.2): keeps the control channel <c>/tunnel/control</c> open with the device
/// token (reconnecting after a loss), remembers the host ticket of every <c>sessionOpened</c>, and on each
/// <c>laneRequested</c> opens that lane and hands it to <c>acceptLane</c> as a plain stream. The Agent passes the
/// stream to its <c>SecureServer</c>, exactly like an accepted TCP connection: the relay never sees plaintext.
/// </summary>
public sealed class RelayHostLink : IAsyncDisposable
{
    private readonly RelayEndpoint _relay;
    private readonly Func<Stream, string, Task> _acceptLane;
    private readonly IRelaySocketFactory _sockets;
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();
    private readonly ConcurrentDictionary<Guid, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<Task, byte> _lanes = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _run = Task.CompletedTask;
    private Task _sweeper = Task.CompletedTask;
    private WebSocket? _control;
    private volatile bool _isConnected;
    private int _disposed;

    /// <param name="acceptLane">Serves one lane (stream owned by the callee) with a description of the peer for logs.</param>
    /// <param name="sockets">Null for <see cref="ClientWebSocketFactory"/>.</param>
    public RelayHostLink(RelayEndpoint relay, Func<Stream, string, Task> acceptLane, IRelaySocketFactory? sockets = null)
    {
        _relay = relay;
        _acceptLane = acceptLane;
        _sockets = sockets ?? new ClientWebSocketFactory();
    }

    /// <summary>First delay before reconnecting the control channel; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A control message bigger than this closes the channel: the relay's messages are tiny JSON lines.</summary>
    public int MaxControlMessageBytes { get; init; } = 64 * 1024;

    /// <summary>Most remembered sessions; over it the oldest session without an open lane is forgotten first.</summary>
    public int MaxTrackedSessions { get; init; } = 1024;

    /// <summary>How often expired sessions are swept, besides on every new <c>sessionOpened</c>.</summary>
    public TimeSpan SessionSweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a session is kept after its last lane closed, at least. The relay accepts new lanes for a grace period
    /// after the last one closed, so the ticket is still useful past <c>ExpiresAt</c> then.
    /// </summary>
    public TimeSpan SessionRetention { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Clock of the session sweep; tests replace it.</summary>
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Sessions remembered right now (tests).</summary>
    internal int TrackedSessionCount => _sessions.Count;

    /// <summary>Forgets expired sessions and enforces <see cref="MaxTrackedSessions"/> (tests call it directly).</summary>
    internal void SweepSessions()
    {
        DateTime now = UtcNow();
        foreach ((Guid id, HostSession session) in _sessions)
        {
            if (session.IsExpired(now))
                _sessions.TryRemove(new KeyValuePair<Guid, HostSession>(id, session));
        }
        int excess = _sessions.Count - MaxTrackedSessions;
        if (excess <= 0)
            return;
        foreach (HostSession oldest in _sessions.Values.Where(x => !x.HasOpenLanes).OrderBy(x => x.Opened.ExpiresAt).Take(excess))
            _sessions.TryRemove(new KeyValuePair<Guid, HostSession>(oldest.Opened.SessionId, oldest));
    }

    /// <summary>The control channel is open: the relay shows this machine online and can route sessions to it.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Last error of the control channel (connect refused, token revoked...), null while connected.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on a background thread when <see cref="IsConnected"/> changes.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start()
    {
        _run = Task.Run(RunAsync);
        _sweeper = Task.Run(SweepLoopAsync);
    }

    private async Task SweepLoopAsync()
    {
        using var timer = new PeriodicTimer(SessionSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
                SweepSessions();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunAsync()
    {
        TimeSpan delay = ReconnectDelay;
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using WebSocket control = await _sockets.ConnectAsync(_relay.WebSocketUri(TunnelProtocol.ControlPath), _relay.DeviceToken, _stopping.Token).ConfigureAwait(false);
                _control = control;
                LastError = null;
                SetConnected(true);
                delay = ReconnectDelay;
                await ReadControlAsync(control, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException)
            {
                LastError = e.Message;
            }
            finally
            {
                _control = null;
                SetConnected(false);
            }

            try
            {
                await Task.Delay(delay, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
        }
    }

    private async Task ReadControlAsync(WebSocket control, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            ValueWebSocketReceiveResult result = await control.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                LastError = control.CloseStatusDescription ?? "Relay closed the control channel.";
                return;
            }
            if (message.Length + result.Count > MaxControlMessageBytes)
            {
                // Nothing legitimate is this big: stop buffering and drop the channel (the loop reconnects).
                await CloseTooBigAsync(control).ConfigureAwait(false);
                throw new InvalidOperationException($"Relay control message is larger than {MaxControlMessageBytes} bytes.");
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType == WebSocketMessageType.Text)
                OnControlMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private static async Task CloseTooBigAsync(WebSocket control)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await control.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "control message too large", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
        control.Abort();
    }

    private void OnControlMessage(string text)
    {
        JObject json = JObject.Parse(text);
        switch (json.Value<string>("type"))
        {
            case TunnelProtocol.SessionOpenedMessage:
                TunnelSessionOpenedMessageVM opened = json.ToObject<TunnelSessionOpenedMessageVM>(JsonSerializer.Create(_json))!;
                _sessions[opened.SessionId] = new HostSession(opened);
                SweepSessions(); // Drops expired entries and enforces the cap with the new one counted.
                break;
            case TunnelProtocol.LaneRequestedMessage:
                TunnelLaneRequestedMessageVM request = json.ToObject<TunnelLaneRequestedMessageVM>(JsonSerializer.Create(_json))!;
                if (_sessions.TryGetValue(request.SessionId, out HostSession? session)
                    && request.Lane is >= 0 and < TunnelProtocol.MaxLanes)
                {
                    session.LaneStarted();
                    Task lane = Task.Run(() => ServeLaneAsync(session, request.Lane));
                    _lanes[lane] = 0;
                    _ = lane.ContinueWith(x => _lanes.TryRemove(x, out _), TaskScheduler.Default);
                }
                break;
            // Unknown types come from a newer relay: ignore them.
        }
    }

    private async Task ServeLaneAsync(HostSession hostSession, int lane)
    {
        try
        {
            await ServeLaneCoreAsync(hostSession.Opened, lane).ConfigureAwait(false);
        }
        finally
        {
            hostSession.LaneEnded(UtcNow(), SessionRetention);
        }
    }

    private async Task ServeLaneCoreAsync(TunnelSessionOpenedMessageVM session, int lane)
    {
        // The ticket goes in the Authorization header, not the URL: URLs end up in proxy and access logs.
        Uri uri = _relay.WebSocketUri($"{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={session.SessionId}&{TunnelProtocol.LaneQuery}={lane}");
        WebSocket socket;
        try
        {
            socket = await _sockets.ConnectAsync(uri, session.Ticket, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or InvalidOperationException or OperationCanceledException)
        {
            // The session is gone on the relay (expired, revoked): forget its ticket. The client lane times out.
            if (!_stopping.IsCancellationRequested)
                _sessions.TryRemove(session.SessionId, out _);
            return;
        }
        try
        {
            await _acceptLane(new WebSocketLaneStream(socket), $"relay:{session.ClientDeviceId}").ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // The Agent owns the stream and already closed it; one broken lane must not stop the link.
        }
    }

    /// <summary>A session the relay told this host about, with how many of its lanes are being served.</summary>
    private sealed class HostSession(TunnelSessionOpenedMessageVM opened)
    {
        private readonly object _lock = new();
        private int _openLanes;
        private DateTime _keepUntil = opened.ExpiresAt.ToUniversalTime();

        public TunnelSessionOpenedMessageVM Opened { get; } = opened;

        public bool HasOpenLanes
        {
            get
            {
                lock (_lock)
                    return _openLanes > 0;
            }
        }

        public void LaneStarted()
        {
            lock (_lock)
                _openLanes++;
        }

        public void LaneEnded(DateTime now, TimeSpan retention)
        {
            lock (_lock)
            {
                _openLanes--;
                DateTime until = now + retention;
                if (until > _keepUntil)
                    _keepUntil = until;
            }
        }

        /// <summary>Past its deadline (and the retention after the last lane) with no lane open: its ticket is dead.</summary>
        public bool IsExpired(DateTime now)
        {
            lock (_lock)
                return _openLanes == 0 && now >= _keepUntil;
        }
    }

    private void SetConnected(bool connected)
    {
        if (_isConnected == connected)
            return;
        _isConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    /// <summary>Closes the control channel and stops reconnecting; lanes already handed out stay with the Agent.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stopping.Cancel();
        _control?.Abort();
        try
        {
            await _run.ConfigureAwait(false);
            await _sweeper.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(_lanes.Keys).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
