using System.Collections.Concurrent;
using System.Net.WebSockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Newtonsoft.Json;

using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Common.Presence.Interfaces;
using ProcBridge.Relay.Services.Common.Security;
using ProcBridge.Relay.Services.Tunnel.Enums;
using ProcBridge.Relay.Services.Tunnel.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Models;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Services.Tunnel;

/// <summary>
/// In-memory tunnel registry (singleton). State lives only in this process: a relay restart drops every session and
/// control channel, and peers reconnect. Running several relay instances needs a shared registry (backlog).
/// </summary>
public sealed class TunnelHub : ITunnelHub, IDevicePresence
{
    private const string TicketPrefix = "pbt_";

    private readonly ConcurrentDictionary<Guid, TunnelControlChannel> _controls = new();
    private readonly ConcurrentDictionary<Guid, TunnelSession> _sessions = new();
    private readonly JsonSerializerSettings _json = RelayJsonSettings.Create();
    private readonly TunnelOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<TunnelHub> _logger;

    public TunnelHub(IOptions<TunnelOptions> options, TimeProvider time, ILogger<TunnelHub> logger)
    {
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public bool IsOnline(Guid deviceId) => _controls.ContainsKey(deviceId);

    public async Task RunControlChannelAsync(Guid deviceId, WebSocket socket, CancellationToken cancellationToken)
    {
        var channel = new TunnelControlChannel(socket);
        TunnelControlChannel? replaced = null;
        _controls.AddOrUpdate(deviceId, channel, (_, old) =>
        {
            replaced = old;
            return channel;
        });
        replaced?.Abort();
        _logger.LogInformation("Máy {DeviceId} đã mở kênh điều khiển tunnel.", deviceId);
        try
        {
            await channel.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Peer vanished or the request was aborted: nothing to report beyond the log line below.
        }
        finally
        {
            _controls.TryRemove(new KeyValuePair<Guid, TunnelControlChannel>(deviceId, channel));
            _logger.LogInformation("Máy {DeviceId} đã đóng kênh điều khiển tunnel.", deviceId);
        }
    }

    public async Task<TunnelSessionVM> OpenSessionAsync(
        Guid clientDeviceId,
        string? clientPublicKey,
        Guid shareId,
        Guid hostDeviceId,
        string? hostPublicKey,
        CancellationToken cancellationToken)
    {
        PurgeDeadSessions();
        if (!_controls.TryGetValue(hostDeviceId, out var control))
            throw ServiceException.Conflict(RelayErrors.DeviceOffline, "Máy chia sẻ đang không kết nối relay.");

        var expiresAt = Now + _options.SessionLifetime;
        var clientTicket = SecretTokens.Create(TicketPrefix);
        var hostTicket = SecretTokens.Create(TicketPrefix);
        var session = new TunnelSession(Guid.NewGuid(), shareId, clientDeviceId, hostDeviceId, clientTicket, hostTicket, expiresAt);
        _sessions[session.Id] = session;

        var message = new TunnelSessionOpenedMessageVM(
            TunnelProtocol.SessionOpenedMessage, session.Id, hostTicket, expiresAt, shareId, clientDeviceId, clientPublicKey);
        try
        {
            await control.SendTextAsync(JsonConvert.SerializeObject(message, _json), cancellationToken);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            _sessions.TryRemove(session.Id, out _);
            throw ServiceException.Conflict(RelayErrors.DeviceOffline, "Máy chia sẻ đang không kết nối relay.");
        }

        _logger.LogInformation(
            "Mở phiên tunnel {SessionId}: máy {ClientDeviceId} -> chia sẻ {ShareId} trên máy {HostDeviceId}.",
            session.Id, clientDeviceId, shareId, hostDeviceId);
        return new TunnelSessionVM(session.Id, clientTicket, expiresAt, hostDeviceId, hostPublicKey);
    }

    public TunnelLaneReservation ReserveLane(Guid sessionId, string ticket, int lane)
    {
        PurgeDeadSessions();
        if (!_sessions.TryGetValue(sessionId, out var session) || !session.AcceptsLanes(Now))
            throw ServiceException.NotFound(RelayErrors.TunnelSessionNotFound, "Phiên tunnel không tồn tại hoặc đã hết hạn.");
        var side = session.MatchTicket(ticket ?? string.Empty)
            ?? throw ServiceException.NotFound(RelayErrors.TunnelSessionNotFound, "Phiên tunnel không tồn tại hoặc đã hết hạn.");
        if (lane < 0 || lane >= TunnelProtocol.MaxLanes)
            throw ServiceException.BadRequest(RelayErrors.TunnelLaneInvalid, $"Lane phải trong khoảng 0-{TunnelProtocol.MaxLanes - 1}.");
        var pair = session.TryReserve(lane, side)
            ?? throw ServiceException.Conflict(RelayErrors.TunnelLaneBusy, "Lane này đã có kết nối ở phía đó.");
        return new TunnelLaneReservation(session, pair, side, lane);
    }

    public void ReleaseLane(TunnelLaneReservation reservation)
    {
        reservation.Pair.Abort();
        reservation.Session.RemoveLane(reservation.Lane, reservation.Pair, Now, _options.SessionLifetime);
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
                partner = await pair.PartnerAsync(reservation.Side).WaitAsync(_options.PairTimeout, cancellationToken);
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
            reservation.Session.RemoveLane(reservation.Lane, pair, Now, _options.SessionLifetime);
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

    private void PurgeDeadSessions()
    {
        var now = Now;
        foreach (var (id, session) in _sessions)
        {
            if (session.IsDead(now))
                _sessions.TryRemove(new KeyValuePair<Guid, TunnelSession>(id, session));
        }
    }
}
