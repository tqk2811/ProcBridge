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
    private readonly ConcurrentDictionary<Guid, TunnelSessionOpenedMessageVM> _sessions = new();
    private readonly ConcurrentDictionary<Task, byte> _lanes = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task _run = Task.CompletedTask;
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

    /// <summary>The control channel is open: the relay shows this machine online and can route sessions to it.</summary>
    public bool IsConnected => _isConnected;

    /// <summary>Last error of the control channel (connect refused, token revoked...), null while connected.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised on a background thread when <see cref="IsConnected"/> changes.</summary>
    public event Action<bool>? ConnectionChanged;

    public void Start() => _run = Task.Run(RunAsync);

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
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            if (result.MessageType == WebSocketMessageType.Text)
                OnControlMessage(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void OnControlMessage(string text)
    {
        JObject json = JObject.Parse(text);
        switch (json.Value<string>("type"))
        {
            case TunnelProtocol.SessionOpenedMessage:
                TunnelSessionOpenedMessageVM opened = json.ToObject<TunnelSessionOpenedMessageVM>(JsonSerializer.Create(_json))!;
                _sessions[opened.SessionId] = opened;
                break;
            case TunnelProtocol.LaneRequestedMessage:
                TunnelLaneRequestedMessageVM request = json.ToObject<TunnelLaneRequestedMessageVM>(JsonSerializer.Create(_json))!;
                if (_sessions.TryGetValue(request.SessionId, out TunnelSessionOpenedMessageVM? session)
                    && request.Lane is >= 0 and < TunnelProtocol.MaxLanes)
                {
                    Task lane = Task.Run(() => ServeLaneAsync(session, request.Lane));
                    _lanes[lane] = 0;
                    _ = lane.ContinueWith(x => _lanes.TryRemove(x, out _), TaskScheduler.Default);
                }
                break;
            // Unknown types come from a newer relay: ignore them.
        }
    }

    private async Task ServeLaneAsync(TunnelSessionOpenedMessageVM session, int lane)
    {
        Uri uri = _relay.WebSocketUri(
            $"{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={session.SessionId}"
            + $"&{TunnelProtocol.TicketQuery}={Uri.EscapeDataString(session.Ticket)}&{TunnelProtocol.LaneQuery}={lane}");
        WebSocket socket;
        try
        {
            socket = await _sockets.ConnectAsync(uri, null, _stopping.Token).ConfigureAwait(false);
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
        }
        catch (OperationCanceledException)
        {
        }
        await Task.WhenAll(_lanes.Keys).ConfigureAwait(false);
        _stopping.Dispose();
    }
}
