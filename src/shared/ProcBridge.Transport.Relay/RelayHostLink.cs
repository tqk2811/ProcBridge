using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;
using ProcBridge.Transport.Relay.Interfaces;
using ProcBridge.Transport.Relay.Models;

namespace ProcBridge.Transport.Relay;

/// <summary>
/// Machine B side of the relay tunnel (plan 8.2): keeps the control channel <c>/tunnel/control</c> open (reconnecting
/// after a loss), proving its identity key with a Noise handshake each time so the relay routes sessions for its HostId
/// here and nowhere else; remembers the host ticket of every <c>sessionOpened</c>, and on each
/// <c>laneRequested</c> opens that lane and hands it to <c>acceptLane</c> as a plain stream. The Agent passes the
/// stream to its <c>SecureServer</c>, exactly like an accepted TCP connection: the relay never sees plaintext.
/// </summary>
public sealed class RelayHostLink : IAsyncDisposable
{
    private readonly RelayEndpoint _relay;
    private readonly IdentityKey _identity;
    private readonly Func<Stream, string, Task> _acceptLane;
    private readonly IRelaySocketFactory _sockets;
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();
    private readonly ConcurrentDictionary<Guid, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<Task, byte> _lanes = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _sendWake = new(0, int.MaxValue);
    // Pairing slot state, under _pairingLock: the code waiting for the relay's answer, and the one the relay confirmed.
    private readonly object _pairingLock = new();
    private bool _pairingEnabled;
    private string? _pendingCode;
    private string? _pendingKey;
    private string? _activeCode;
    private string? _activeKey;
    private DateTime? _activeExpiresAt;
    private int _rejectedInARow;
    private DateTime? _renewAt;
    private DateTime? _retryAt;
    private TimeSpan _retryDelay;
    private string? _pairingError;
    // Slot key the relay holds for this host as far as the send loop knows (null: none). Under _pairingLock.
    private string? _sentKey;
    private Task _sender = Task.CompletedTask;
    private Task _run = Task.CompletedTask;
    private Task _sweeper = Task.CompletedTask;
    private Task _pairingTimer = Task.CompletedTask;
    private WebSocket? _control;
    private volatile bool _isConnected;
    private int _disposed;

    /// <param name="identity">This machine's identity key (borrowed): proves the HostId on the control channel.</param>
    /// <param name="acceptLane">Serves one lane (stream owned by the callee) with a description of the peer for logs.</param>
    /// <param name="sockets">Null for <see cref="ClientWebSocketFactory"/>.</param>
    public RelayHostLink(RelayEndpoint relay, IdentityKey identity, Func<Stream, string, Task> acceptLane, IRelaySocketFactory? sockets = null)
    {
        _relay = relay;
        _identity = identity;
        _acceptLane = acceptLane;
        _sockets = sockets ?? new ClientWebSocketFactory();
    }

    /// <summary>First delay before reconnecting the control channel; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the relay may take to finish the control handshake before the link retries.</summary>
    public TimeSpan ControlHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

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

    /// <summary>How often an open pairing slot is checked for expiry (a new code is opened once it expired).</summary>
    public TimeSpan PairingCheckInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Clock of the session sweep and of the pairing slot expiry; tests replace it.</summary>
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

    /// <summary>HostId machines A use to reach this machine through the relay (fingerprint of the identity key).</summary>
    public string HostId => _identity.Fingerprint;

    /// <summary>The control channel is open and proven: the relay can route sessions for <see cref="HostId"/> to it.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Last error of the control channel (connect refused, handshake failed...), null while connected.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on a background thread when <see cref="IsConnected"/> changes.</summary>
    public event Action<bool>? ConnectionChanged;

    /// <summary>
    /// Pairing invite code machines A type to find this machine on the relay (<c>1234-5678</c>), once the relay opened
    /// the slot; null while pairing through the relay is off, not connected, or a new code waits for the relay.
    /// </summary>
    public string? PairingCode
    {
        get
        {
            lock (_pairingLock)
                return _activeCode is null ? null : RelayPairingCode.Format(_activeCode);
        }
    }

    /// <summary>
    /// When the relay drops the slot of <see cref="PairingCode"/> (UTC, on this machine's clock: receive time plus the
    /// lifetime the relay gave). A new code is opened a little earlier (<see cref="PairingRenewMargin"/>).
    /// </summary>
    public DateTime? PairingCodeExpiresAt
    {
        get
        {
            lock (_pairingLock)
                return _activeExpiresAt;
        }
    }

    /// <summary>
    /// Why no code can be shown (the relay refused several codes in a row); null when fine. The link keeps retrying
    /// with a growing delay.
    /// </summary>
    public string? PairingError
    {
        get
        {
            lock (_pairingLock)
                return _pairingError;
        }
    }

    /// <summary>Raised on a background thread when <see cref="PairingCode"/> or <see cref="PairingError"/> changes.</summary>
    public event Action? PairingCodeChanged;

    /// <summary>A new code is asked for this long before the slot expires (at most a quarter of the slot lifetime).</summary>
    public TimeSpan PairingRenewMargin { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Codes the relay may refuse in a row before the link reports <see cref="PairingError"/> and backs off.</summary>
    public int MaxPairingRejections { get; init; } = 5;

    /// <summary>First delay after <see cref="MaxPairingRejections"/>; doubles up to <see cref="MaxPairingRetryDelay"/>.</summary>
    public TimeSpan PairingRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxPairingRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Opens (true) or closes (false) a pairing slot on the relay, so machines A that only reach this machine through the
    /// relay find it by an invite code (plan 9). While on, the link keeps a slot open: a new code after the relay drops
    /// the previous one (expired, used by a machine A, control channel reconnected). The pairing itself then runs end to
    /// end on a lane like on the LAN; the Agent must accept pairings meanwhile.
    /// </summary>
    public void SetPairingSlot(bool enabled)
    {
        bool changed;
        lock (_pairingLock)
        {
            if (_pairingEnabled == enabled)
                return;
            _pairingEnabled = enabled;
            _rejectedInARow = 0;
            _retryDelay = TimeSpan.Zero;
            _retryAt = null;
            changed = _pairingError is not null;
            _pairingError = null;
        }
        if (enabled)
            RotatePairingCode();
        else
            ClearPairingCode(changed);
    }

    /// <summary>Picks a new code (shown once the relay confirms it) and wakes the send loop. No-op while pairing is off.</summary>
    private void RotatePairingCode()
    {
        bool hadCode;
        lock (_pairingLock)
        {
            if (!_pairingEnabled)
                return;
            hadCode = _activeCode is not null;
            _pendingCode = RelayPairingCode.Generate();
            _pendingKey = RelayPairingCode.DeriveLookupKey(_pendingCode);
            _activeCode = _activeKey = null;
            _activeExpiresAt = _renewAt = null;
            _retryAt = null;
        }
        if (hadCode)
            PairingCodeChanged?.Invoke();
        WakeSender();
    }

    private void ClearPairingCode(bool forceNotify = false)
    {
        bool hadCode;
        lock (_pairingLock)
        {
            hadCode = _activeCode is not null;
            _pendingCode = _pendingKey = _activeCode = _activeKey = null;
            _activeExpiresAt = _renewAt = null;
        }
        if (hadCode || forceNotify)
            PairingCodeChanged?.Invoke();
        WakeSender();
    }

    /// <summary>The channel dropped: forget the codes (the relay dropped the slot) but stay enabled for the reconnect.</summary>
    private void ClearPairingCodeKeepEnabled()
    {
        bool hadCode;
        lock (_pairingLock)
        {
            hadCode = _activeCode is not null;
            _pendingCode = _pendingKey = _activeCode = _activeKey = null;
            _activeExpiresAt = _renewAt = null;
        }
        if (hadCode)
            PairingCodeChanged?.Invoke();
    }

    private void OnPairingSlotState(string type, TunnelPairingSlotStateMessageVM state)
    {
        switch (type)
        {
            case TunnelProtocol.PairingSlotRegisteredMessage:
                bool notify = false;
                lock (_pairingLock)
                {
                    if (_pairingEnabled && state.LookupKey == _pendingKey)
                    {
                        DateTime received = UtcNow();
                        TimeSpan lifetime = state.ExpiresInSeconds is { } seconds
                            ? TimeSpan.FromSeconds(seconds)
                            : (state.ExpiresAt?.ToUniversalTime() ?? received) - DateTime.UtcNow; // Older relay: only its clock.
                        if (lifetime < TimeSpan.Zero)
                            lifetime = TimeSpan.Zero;
                        TimeSpan margin = TimeSpan.FromTicks(Math.Min(PairingRenewMargin.Ticks, lifetime.Ticks / 4));
                        _activeCode = _pendingCode;
                        _activeKey = _pendingKey;
                        _activeExpiresAt = received + lifetime;
                        _renewAt = received + lifetime - margin;
                        _pendingCode = _pendingKey = null;
                        _rejectedInARow = 0;
                        _retryDelay = TimeSpan.Zero;
                        _pairingError = null;
                        notify = true;
                    }
                    else
                    {
                        // A slot this link no longer wants (replaced or turned off meanwhile) is what the relay holds now:
                        // the send loop puts the right one back, or cancels it.
                        _sentKey = state.LookupKey;
                    }
                }
                if (notify)
                    PairingCodeChanged?.Invoke();
                WakeSender();
                break;
            case TunnelProtocol.PairingSlotRejectedMessage:
                bool gaveUp;
                lock (_pairingLock)
                {
                    if (state.LookupKey != _pendingKey)
                        return;
                    _sentKey = null; // The relay holds nothing for this host now.
                    gaveUp = ++_rejectedInARow > MaxPairingRejections;
                    if (gaveUp)
                    {
                        // Do not hammer the relay; say why no code shows and try again later.
                        _retryDelay = _retryDelay == TimeSpan.Zero
                            ? PairingRetryDelay
                            : TimeSpan.FromTicks(Math.Min(_retryDelay.Ticks * 2, MaxPairingRetryDelay.Ticks));
                        _retryAt = UtcNow() + _retryDelay;
                        _pendingCode = _pendingKey = null;
                        _pairingError = $"Relay từ chối {_rejectedInARow} mã ghép cặp liên tiếp; thử lại sau {_retryDelay.TotalSeconds:0} giây.";
                    }
                }
                if (gaveUp)
                    PairingCodeChanged?.Invoke();
                else
                    RotatePairingCode();
                break;
            case TunnelProtocol.PairingSlotClaimedMessage:
                lock (_pairingLock)
                {
                    if (state.LookupKey != _activeKey && state.LookupKey != _pendingKey)
                        return;
                    _sentKey = null; // Used up on the relay.
                }
                // Single use on the relay: the next machine A needs a new code.
                RotatePairingCode();
                break;
        }
    }

    private async Task PairingLoopAsync()
    {
        using var timer = new PeriodicTimer(PairingCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                bool rotate;
                lock (_pairingLock)
                {
                    DateTime now = UtcNow();
                    rotate = _pairingEnabled && _isConnected
                        && ((_renewAt is { } renew && now >= renew) || (_retryAt is { } retry && now >= retry));
                }
                if (rotate)
                    RotatePairingCode();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void WakeSender() => _sendWake.Release();

    /// <summary>
    /// The only writer of pairing messages: each time it is woken it sends the slot the link wants NOW (or a cancel) when
    /// that differs from what it last sent. Sending the latest state instead of queued intents means a slow or reordered
    /// wake-up can never leave a stale code or a slot that should have been closed.
    /// </summary>
    private async Task SendLoopAsync()
    {
        try
        {
            while (true)
            {
                await _sendWake.WaitAsync(_stopping.Token).ConfigureAwait(false);
                WebSocket? control = _control;
                if (control is null || !_isConnected)
                    continue; // Reconnecting asks for a fresh slot.
                TunnelPairingSlotMessageVM message;
                lock (_pairingLock)
                {
                    string? wanted = _pairingEnabled ? _pendingKey ?? _activeKey : null;
                    if (wanted == _sentKey)
                        continue;
                    message = wanted is null
                        ? new TunnelPairingSlotMessageVM(TunnelProtocol.PairingSlotCancelMessage, null)
                        : new TunnelPairingSlotMessageVM(TunnelProtocol.PairingSlotMessage, wanted);
                    _sentKey = wanted;
                }
                await SendControlAsync(control, message).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendControlAsync(WebSocket control, object message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, _json));
        try
        {
            await control.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _stopping.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The channel broke: the read loop sees it too and reconnects, which asks for a slot again.
        }
    }

    public void Start()
    {
        _run = Task.Run(RunAsync);
        _sweeper = Task.Run(SweepLoopAsync);
        _pairingTimer = Task.Run(PairingLoopAsync);
        _sender = Task.Run(SendLoopAsync);
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
                using WebSocket control = await _sockets.ConnectAsync(_relay.WebSocketUri(TunnelProtocol.ControlPath), null, _stopping.Token).ConfigureAwait(false);
                _control = control;
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                {
                    handshake.CancelAfter(ControlHandshakeTimeout);
                    await RelayControlHandshake.RunAsync(control, _identity, MaxControlMessageBytes, handshake.Token).ConfigureAwait(false);
                }
                LastError = null;
                SetConnected(true);
                delay = ReconnectDelay;
                // A new control channel has no slot on the relay (it may be a restarted relay): open one with a new code.
                lock (_pairingLock)
                    _sentKey = null;
                RotatePairingCode();
                await ReadControlAsync(control, _stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or InvalidOperationException or JsonException or OperationCanceledException or InvalidDataException)
            {
                LastError = e.Message;
            }
            finally
            {
                _control = null;
                SetConnected(false);
                // The relay dropped the slot with the channel: do not show a code nobody can use.
                lock (_pairingLock)
                    _sentKey = null;
                ClearPairingCodeKeepEnabled();
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
        string? type = json.Value<string>("type");
        switch (type)
        {
            case TunnelProtocol.PairingSlotRegisteredMessage:
            case TunnelProtocol.PairingSlotRejectedMessage:
            case TunnelProtocol.PairingSlotClaimedMessage:
                OnPairingSlotState(type, json.ToObject<TunnelPairingSlotStateMessageVM>(JsonSerializer.Create(_json))!);
                break;
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
            await _acceptLane(new WebSocketLaneStream(socket), $"relay:{session.SessionId}").ConfigureAwait(false);
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
            await _pairingTimer.ConfigureAwait(false);
            await _sender.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(_lanes.Keys).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
