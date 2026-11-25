using System.Collections.Concurrent;
using System.Net.WebSockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Services.Tunnel.Enums;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Auth;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>
/// In-memory tunnel registry (singleton). The relay keeps no accounts and stores nothing: a host is known only while
/// its control channel is open, under the HostId it proved with a Noise handshake. A relay restart drops every session
/// and control channel, and peers reconnect. Owns its relay identity key (fresh per process).
/// </summary>
public sealed class TunnelHub : ITunnelHub, IDisposable
{
    private const string TicketPrefix = RelayAuth.TicketPrefix;

    private readonly ConcurrentDictionary<string, TunnelControlChannel> _controls = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
    private readonly object _countLock = new();
    private readonly Dictionary<string, int> _controlsPerAddress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lanesPerAddress = new(StringComparer.Ordinal);
    // Pairing slots: lookup key -> slot, and the one key of each host. Both under _slotLock.
    private readonly object _slotLock = new();
    private readonly Dictionary<string, PairingSlot> _slotsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _slotKeyByHost = new(StringComparer.Ordinal);
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();
    private readonly TunnelOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TunnelHub> _logger;
    // Only gives hosts a key to run the handshake against; nobody pins it, so a new one per process is fine.
    private readonly IdentityKey _relayKey = IdentityKey.Generate();

    public TunnelHub(IOptions<TunnelOptions> options, TimeProvider time, ILogger<TunnelHub> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>True while a host with this HostId has a proven control channel (tests, diagnostics).</summary>
    public bool IsOnline(string hostId) => _controls.ContainsKey(hostId);

    /// <summary>True while the host has an open (unexpired, unused) pairing slot (tests, diagnostics).</summary>
    public bool HasPairingSlot(string hostId)
    {
        lock (_slotLock)
        {
            PurgeExpiredSlots();
            return _slotKeyByHost.ContainsKey(hostId);
        }
    }

    public bool CanOpenControlChannel(string clientAddress)
    {
        lock (_countLock)
            return _controlsPerAddress.GetValueOrDefault(clientAddress) < _options.MaxControlChannelsPerAddress;
    }

    public async Task RunControlChannelAsync(WebSocket socket, string clientAddress, CancellationToken cancellationToken)
    {
        if (!TryAcquire(_controlsPerAddress, clientAddress, _options.MaxControlChannelsPerAddress))
        {
            await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "too many control channels");
            return;
        }
        try
        {
            var channel = new TunnelControlChannel(socket);
            string hostId;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.ControlHandshakeTimeout);
                byte[] hostKey = await channel.ProveHostKeyAsync(_relayKey, timeout.Token);
                hostId = KeyFingerprint.Compute(hostKey);
            }
            catch (Exception ex) when (ex is InvalidDataException or WebSocketException or OperationCanceledException)
            {
                _logger.LogInformation("Kênh điều khiển từ {Address} không chứng minh được khoá: {Reason}", clientAddress, ex.Message);
                await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "handshake failed");
                return;
            }

            await RunProvenChannelAsync(hostId, channel, cancellationToken);
        }
        finally
        {
            Release(_controlsPerAddress, clientAddress);
        }
    }

    private async Task RunProvenChannelAsync(string hostId, TunnelControlChannel channel, CancellationToken cancellationToken)
    {
        TunnelControlChannel? replaced = null;
        _controls.AddOrUpdate(hostId, channel, (_, old) =>
        {
            replaced = old;
            return channel;
        });
        // Only the owner of the key can get here, so the newer channel is the same host reconnecting.
        replaced?.Abort();
        _logger.LogInformation("Máy {HostId} đã mở kênh điều khiển tunnel.", hostId);
        try
        {
            var registered = new TunnelHostRegisteredMessageVM(TunnelProtocol.HostRegisteredMessage, hostId);
            await channel.SendTextAsync(JsonConvert.SerializeObject(registered, _json), cancellationToken);
            await channel.RunAsync(text => OnHostMessageAsync(hostId, channel, text, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Peer vanished or the request was aborted: nothing to report beyond the log line below.
        }
        finally
        {
            RemoveSlotOf(hostId, channel);
            _controls.TryRemove(new KeyValuePair<string, TunnelControlChannel>(hostId, channel));
            _logger.LogInformation("Máy {HostId} đã đóng kênh điều khiển tunnel.", hostId);
        }
    }

    /// <summary>A text message from a proven host: opens or closes its pairing slot. Unknown types are ignored.</summary>
    private async Task OnHostMessageAsync(string hostId, TunnelControlChannel channel, string text, CancellationToken cancellationToken)
    {
        TunnelPairingSlotMessageVM? message;
        try
        {
            message = JsonConvert.DeserializeObject<TunnelPairingSlotMessageVM>(text, _json);
        }
        catch (JsonException)
        {
            return; // Not ours to fix: the host sends JSON lines only.
        }
        switch (message?.Type)
        {
            case TunnelProtocol.PairingSlotMessage:
                await RegisterSlotAsync(hostId, channel, message.LookupKey, cancellationToken);
                break;
            case TunnelProtocol.PairingSlotCancelMessage:
                RemoveSlotOf(hostId, channel);
                break;
        }
    }

    private async Task RegisterSlotAsync(string hostId, TunnelControlChannel channel, string? lookupKey, CancellationToken cancellationToken)
    {
        DateTime? expiresAt = null;
        if (RelayPairingCode.IsLookupKey(lookupKey))
        {
            lock (_slotLock)
            {
                PurgeExpiredSlots();
                // Another host holding the same key (codes are short) must not be displaced: this host picks a new code.
                if (!_slotsByKey.TryGetValue(lookupKey!, out var taken) || taken.HostId == hostId)
                {
                    RemoveSlotLocked(hostId);
                    expiresAt = Now + _options.PairingSlotLifetime;
                    _slotsByKey[lookupKey!] = new PairingSlot(hostId, channel, expiresAt.Value);
                    _slotKeyByHost[hostId] = lookupKey!;
                }
            }
        }
        var reply = expiresAt is null
            ? new TunnelPairingSlotStateMessageVM(TunnelProtocol.PairingSlotRejectedMessage, lookupKey ?? string.Empty)
            : new TunnelPairingSlotStateMessageVM(TunnelProtocol.PairingSlotRegisteredMessage, lookupKey!, expiresAt);
        if (expiresAt is not null)
            _logger.LogInformation("Máy {HostId} mở chỗ ghép cặp tới {ExpiresAt:u}.", hostId, expiresAt);
        await channel.SendTextAsync(JsonConvert.SerializeObject(reply, _json), cancellationToken);
    }

    /// <summary>Drops the slot of <paramref name="hostId"/> if it belongs to <paramref name="channel"/> (not a newer channel).</summary>
    private void RemoveSlotOf(string hostId, TunnelControlChannel channel)
    {
        lock (_slotLock)
        {
            if (_slotKeyByHost.TryGetValue(hostId, out var key) && _slotsByKey.TryGetValue(key, out var slot) && slot.Channel == channel)
                RemoveSlotLocked(hostId);
        }
    }

    private void RemoveSlotLocked(string hostId)
    {
        if (_slotKeyByHost.Remove(hostId, out var key))
            _slotsByKey.Remove(key);
    }

    private void PurgeExpiredSlots()
    {
        var now = Now;
        foreach (var (key, slot) in _slotsByKey.Where(x => x.Value.ExpiresAt <= now).ToList())
        {
            _slotsByKey.Remove(key);
            _slotKeyByHost.Remove(slot.HostId);
        }
    }

    public async Task<TunnelPairingSessionVM> OpenPairingSessionAsync(string lookupKey, string clientAddress, CancellationToken cancellationToken)
    {
        PairingSlot? slot;
        lock (_slotLock)
        {
            PurgeExpiredSlots();
            // Single use: whoever finds the slot takes it, right or wrong, so a code cannot be tried twice.
            if (_slotsByKey.Remove(lookupKey, out slot))
                _slotKeyByHost.Remove(slot.HostId);
        }
        if (slot is null)
            throw ServiceException.NotFound(RelayErrors.PairingSlotNotFound, "Mã ghép cặp không đúng hoặc đã hết hạn.");

        var session = await OpenSessionAsync(slot.HostId, clientAddress, cancellationToken);
        // After sessionOpened (same channel, sends are ordered): the host now knows the ticket and picks a new code.
        var claimed = new TunnelPairingSlotStateMessageVM(TunnelProtocol.PairingSlotClaimedMessage, lookupKey, SessionId: session.SessionId);
        try
        {
            await slot.Channel.SendTextAsync(JsonConvert.SerializeObject(claimed, _json), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The session is open already; the host only misses the hint to show a new code.
        }
        _logger.LogInformation("Phiên ghép cặp {SessionId} từ {Address} tới máy {HostId}.", session.SessionId, clientAddress, slot.HostId);
        return new TunnelPairingSessionVM(session.SessionId, session.Ticket, session.ExpiresAt, slot.HostId);
    }

    /// <summary>An open pairing slot: the host, the control channel that opened it, and its deadline (UTC).</summary>
    private sealed record PairingSlot(string HostId, TunnelControlChannel Channel, DateTime ExpiresAt);

    public async Task<TunnelSessionVM> OpenSessionAsync(string hostId, string clientAddress, CancellationToken cancellationToken)
    {
        PurgeDeadSessions();
        if (!_controls.TryGetValue(hostId, out var control))
            throw ServiceException.Conflict(RelayErrors.HostOffline, "Máy chia sẻ đang không kết nối relay.");

        var expiresAt = Now + _options.SessionLifetime;
        var session = new TunnelSession(
            Guid.NewGuid(), hostId, clientAddress, SecretTokens.Create(TicketPrefix), SecretTokens.Create(TicketPrefix), expiresAt);
        lock (_countLock)
        {
            // Under the lock so two openers cannot both pass the check; dead sessions were purged above.
            var now = Now;
            if (_sessions.Values.Count(x => x.HostId == hostId && !x.IsDead(now)) >= _options.MaxSessionsPerHost)
                throw ServiceException.Conflict(RelayErrors.TooManySessions, "Máy chia sẻ đang có quá nhiều phiên.");
            if (_sessions.Values.Count(x => x.ClientAddress == clientAddress && !x.IsDead(now)) >= _options.MaxSessionsPerAddress)
                throw ServiceException.Conflict(RelayErrors.TooManySessions, "Địa chỉ này đang mở quá nhiều phiên.");
            _sessions[session.Id] = session;
        }

        var message = new TunnelSessionOpenedMessageVM(TunnelProtocol.SessionOpenedMessage, session.Id, session.HostTicket, expiresAt);
        try
        {
            await control.SendTextAsync(JsonConvert.SerializeObject(message, _json), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            AbortSession(session);
            throw ServiceException.Conflict(RelayErrors.HostOffline, "Máy chia sẻ đang không kết nối relay.");
        }

        _logger.LogInformation("Mở phiên tunnel {SessionId} từ {Address} tới máy {HostId}.", session.Id, clientAddress, hostId);
        return new TunnelSessionVM(session.Id, session.ClientTicket, expiresAt);
    }

    public TunnelLaneReservation ReserveLane(Guid sessionId, string ticket, int lane, string clientAddress)
    {
        PurgeDeadSessions();
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.AcceptsLanes(Now))
            throw ServiceException.NotFound(RelayErrors.TunnelSessionNotFound, "Phiên tunnel không tồn tại hoặc đã hết hạn.");
        var side = session.MatchTicket(ticket ?? string.Empty)
            ?? throw ServiceException.NotFound(RelayErrors.TunnelSessionNotFound, "Phiên tunnel không tồn tại hoặc đã hết hạn.");
        if (lane < 0 || lane >= TunnelProtocol.MaxLanes)
            throw ServiceException.BadRequest(RelayErrors.TunnelLaneInvalid, $"Lane phải trong khoảng 0-{TunnelProtocol.MaxLanes - 1}.");
        if (!TryAcquire(_lanesPerAddress, clientAddress, _options.MaxLanesPerAddress))
            throw ServiceException.Conflict(RelayErrors.TooManyLanes, "Địa chỉ này đang mở quá nhiều lane.");
        var pair = session.TryReserve(lane, side);
        if (pair is null)
        {
            Release(_lanesPerAddress, clientAddress);
            throw ServiceException.Conflict(RelayErrors.TunnelLaneBusy, "Lane này đã có kết nối ở phía đó.");
        }
        // The host does not know which lanes the client will open: ask it for this one.
        if (side == TunnelSide.Client && !pair.IsReserved(TunnelSide.Host))
            _ = RequestHostLaneAsync(session, lane);
        return new TunnelLaneReservation(session, pair, side, lane, clientAddress);
    }

    private async Task RequestHostLaneAsync(TunnelSession session, int lane)
    {
        if (!_controls.TryGetValue(session.HostId, out var control))
            return; // The client lane times out waiting for its partner.
        var message = new TunnelLaneRequestedMessageVM(TunnelProtocol.LaneRequestedMessage, session.Id, lane);
        try
        {
            using var timeout = new CancellationTokenSource(_options.PairTimeout);
            await control.SendTextAsync(JsonConvert.SerializeObject(message, _json), timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            _logger.LogInformation("Phiên {SessionId}: không báo được máy chủ mở lane {Lane}.", session.Id, lane);
        }
    }

    private void AbortSession(TunnelSession session)
    {
        session.AbortAll();
        _sessions.TryRemove(new KeyValuePair<Guid, TunnelSession>(session.Id, session));
    }

    public void ReleaseLane(TunnelLaneReservation reservation)
    {
        reservation.Pair.Abort();
        FinishLane(reservation);
    }

    private void FinishLane(TunnelLaneReservation reservation)
    {
        reservation.Session.RemoveLane(reservation.Lane, reservation.Pair, Now, _options.SessionLifetime);
        if (reservation.TryFinish())
            Release(_lanesPerAddress, reservation.ClientAddress);
    }

    public async Task RunLaneAsync(TunnelLaneReservation reservation, WebSocket socket, CancellationToken cancellationToken)
    {
        var pair = reservation.Pair;
        pair.Attach(reservation.Side, socket);
        try
        {
            WebSocket partner;
            try
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pair.AbortToken);
                partner = await pair.PartnerAsync(reservation.Side).WaitAsync(_options.PairTimeout, waiting.Token);
            }
            catch (OperationCanceledException) when (pair.AbortToken.IsCancellationRequested)
            {
                // Torn down while waiting for the other end.
                socket.Abort();
                return;
            }
            catch (TimeoutException)
            {
                _logger.LogInformation(
                    "Phiên {SessionId} lane {Lane}: phía {Side} chờ đầu kia quá hạn.", reservation.SessionId, reservation.Lane, reservation.Side);
                await CloseQuietlyAsync(socket, WebSocketCloseStatus.PolicyViolation, "partner did not join");
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pair.AbortToken);
            try
            {
                await PumpAsync(socket, partner, linked.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // One end broke: tear down the whole lane so the other pump does not hang on a dead peer.
                pair.Abort();
            }
            finally
            {
                pair.MarkPumpDone();
            }

            // My socket is still written by the partner's pump: returning now would cut its last frames (the request
            // ending aborts my socket). Wait for it, but not forever.
            try
            {
                await pair.BothPumpsDone.WaitAsync(_options.CloseGrace, cancellationToken);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                pair.Abort();
            }
        }
        finally
        {
            FinishLane(reservation);
        }
    }

    /// <summary>Copies frames from <paramref name="source"/> to <paramref name="target"/> as-is, then forwards the close.</summary>
    private async Task PumpAsync(WebSocket source, WebSocket target, CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.BufferSize];
        while (true)
        {
            var result = await source.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (target.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await target.CloseOutputAsync(source.CloseStatus ?? WebSocketCloseStatus.NormalClosure, source.CloseStatusDescription, cancellationToken);
                return;
            }
            await target.SendAsync(buffer.AsMemory(0, result.Count), result.MessageType, result.EndOfMessage, cancellationToken);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.CloseOutputAsync(status, description, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private bool TryAcquire(Dictionary<string, int> counts, string key, int max)
    {
        lock (_countLock)
        {
            int current = counts.GetValueOrDefault(key);
            if (current >= max)
                return false;
            counts[key] = current + 1;
            return true;
        }
    }

    private void Release(Dictionary<string, int> counts, string key)
    {
        lock (_countLock)
        {
            if (!counts.TryGetValue(key, out int current))
                return;
            if (current <= 1)
                counts.Remove(key);
            else
                counts[key] = current - 1;
        }
    }

    private void PurgeDeadSessions()
    {
        var now = Now;
        foreach (var (id, session) in _sessions)
        {
            if (session.IsDead(now))
                _sessions.TryRemove(new KeyValuePair<Guid, TunnelSession>(id, session));
        }
    }

    public void Dispose() => _relayKey.Dispose();
}
