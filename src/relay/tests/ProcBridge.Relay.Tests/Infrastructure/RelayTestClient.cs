using System.Net.WebSockets;
using System.Text;

using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Newtonsoft.Json;

using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Services.Tunnel;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.Infrastructure;

/// <summary>Helpers that drive one in-process relay like real peers would.</summary>
public sealed class RelayTestClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly RelayApiFactory _factory;

    public RelayTestClient(RelayApiFactory factory) => _factory = factory;

    /// <param name="remoteIp">Address the relay sees for this caller (test header), null for the test server's default.</param>
    public IRelayApi NewApi(string? remoteIp = null)
    {
        var http = _factory.CreateClient();
        if (remoteIp is not null)
            http.DefaultRequestHeaders.Add(TestRemoteAddressStartupFilter.HeaderName, remoteIp);
        return new RelayApiImplement(http);
    }

    public bool IsOnline(string hostId) => _factory.Services.GetRequiredService<TunnelHub>().IsOnline(hostId);

    /// <summary>
    /// Host side: opens the control channel, proves <paramref name="identity"/> with the Noise XX handshake and returns
    /// the socket with the HostId the relay registered (its <c>hostRegistered</c> message).
    /// </summary>
    public async Task<(WebSocket Control, string HostId)> ConnectControlAsync(IdentityKey identity, string? remoteIp = null)
    {
        var socket = await OpenControlSocketAsync(remoteIp);
        using var handshake = NoiseHandshake.CreatePairing(isInitiator: true, identity, Encoding.ASCII.GetBytes(TunnelProtocol.ControlPrologue));
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(handshake.WriteMessage([]), WebSocketMessageType.Binary, true, cts.Token);
        handshake.ReadMessage(await ReceiveAsync(socket));
        await socket.SendAsync(handshake.WriteMessage([]), WebSocketMessageType.Binary, true, cts.Token);
        var registered = JsonConvert.DeserializeObject<TunnelHostRegisteredMessageVM>(
            await ReceiveControlMessageAsync(socket, TunnelProtocol.HostRegisteredMessage), RelayJsonSettings.Create())!;
        return (socket, registered.HostId);
    }

    /// <summary>A control WebSocket without any handshake (tests of refusals).</summary>
    public async Task<WebSocket> OpenControlSocketAsync(string? remoteIp = null)
    {
        var client = _factory.Server.CreateWebSocketClient();
        if (remoteIp is not null)
            client.ConfigureRequest = r => r.Headers[TestRemoteAddressStartupFilter.HeaderName] = remoteIp;
        return await client.ConnectAsync(new Uri("ws://localhost" + TunnelProtocol.ControlPath), CancellationToken.None);
    }

    /// <summary>Reads control messages until one of type <paramref name="type"/> arrives; returns its JSON.</summary>
    public static async Task<string> ReceiveControlMessageAsync(WebSocket control, string type)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            var result = await control.ReceiveAsync(buffer, cts.Token);
            Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (Newtonsoft.Json.Linq.JObject.Parse(text).Value<string>("type") == type)
                return text;
        }
    }

    public bool HasPairingSlot(string hostId) => _factory.Services.GetRequiredService<TunnelHub>().HasPairingSlot(hostId);

    /// <summary>Host side: sends one JSON control message (pairing slot open/cancel).</summary>
    public static async Task SendControlAsync(WebSocket control, object message)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(message, RelayJsonSettings.Create()));
        await control.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
    }

    /// <summary>Host side: asks for a pairing slot under <paramref name="lookupKey"/> and returns the relay's answer (registered or rejected).</summary>
    public static async Task<TunnelPairingSlotStateMessageVM> RequestPairingSlotAsync(WebSocket control, string? lookupKey)
    {
        await SendControlAsync(control, new TunnelPairingSlotMessageVM(TunnelProtocol.PairingSlotMessage, lookupKey));
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(Timeout);
        while (true)
        {
            var result = await control.ReceiveAsync(buffer, cts.Token);
            Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var type = Newtonsoft.Json.Linq.JObject.Parse(text).Value<string>("type");
            if (type is TunnelProtocol.PairingSlotRegisteredMessage or TunnelProtocol.PairingSlotRejectedMessage)
                return JsonConvert.DeserializeObject<TunnelPairingSlotStateMessageVM>(text, RelayJsonSettings.Create())!;
        }
    }

    public static async Task<TunnelSessionOpenedMessageVM> ReceiveSessionOpenedAsync(WebSocket control)
        => JsonConvert.DeserializeObject<TunnelSessionOpenedMessageVM>(
            await ReceiveControlMessageAsync(control, TunnelProtocol.SessionOpenedMessage), RelayJsonSettings.Create())!;

    /// <summary>Opens a data lane with the ticket in the Authorization header (the way real peers do).</summary>
    public Task<WebSocket> ConnectLaneAsync(Guid sessionId, string ticket, int lane, string? remoteIp = null)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            r.Headers.Authorization = "Bearer " + ticket;
            if (remoteIp is not null)
                r.Headers[TestRemoteAddressStartupFilter.HeaderName] = remoteIp;
        };
        return client.ConnectAsync(
            new Uri($"ws://localhost{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={sessionId}&{TunnelProtocol.LaneQuery}={lane}"),
            CancellationToken.None);
    }

    /// <summary>Legacy form: ticket in the URL.</summary>
    public Task<WebSocket> ConnectLaneWithTicketInQueryAsync(Guid sessionId, string ticket, int lane)
        => _factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri($"ws://localhost{TunnelProtocol.DataPath}?{TunnelProtocol.SessionIdQuery}={sessionId}&{TunnelProtocol.TicketQuery}={Uri.EscapeDataString(ticket)}&{TunnelProtocol.LaneQuery}={lane}"),
            CancellationToken.None);

    public static async Task SendAsync(WebSocket socket, byte[] payload)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, cts.Token);
    }

    /// <summary>Reads one whole binary message.</summary>
    public static async Task<byte[]> ReceiveAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cts.Token);
            Assert.AreEqual(WebSocketMessageType.Binary, result.MessageType);
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return stream.ToArray();
        }
    }

    /// <summary>The next receive ends (close frame or broken socket) instead of waiting.</summary>
    public static async Task AssertClosedAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(Timeout);
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(new byte[1024], cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Assert.Fail("Kết nối không bị đóng.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
