using System.Net.WebSockets;

using Microsoft.AspNetCore.TestHost;

using Newtonsoft.Json;

using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.ViewModels.Accounts;
using ProcBridge.Relay.ViewModels.Devices;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.Infrastructure;

/// <summary>Helpers that drive one in-process relay like real peers would.</summary>
public sealed class RelayTestClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly RelayApiFactory _factory;

    public RelayTestClient(RelayApiFactory factory) => _factory = factory;

    public IRelayApi NewApi(string? token = null)
    {
        var api = new RelayApiImplement(_factory.CreateClient());
        api.SetBearerToken(token);
        return api;
    }

    /// <summary>Registers and signs in a fresh account; returns its session token.</summary>
    public async Task<string> NewAccountAsync(string prefix)
    {
        var userName = prefix + Guid.NewGuid().ToString("N")[..8];
        var api = NewApi();
        await api.Accounts.RegisterAsync(new RegisterAccountRequestVM { UserName = userName, Password = "matkhau-dai-du" });
        return (await api.Accounts.LoginAsync(new LoginRequestVM { UserName = userName, Password = "matkhau-dai-du" })).Token;
    }

    public Task<DeviceRegisteredVM> NewDeviceAsync(string accountToken, string name)
        => NewApi(accountToken).Devices.RegisterAsync(new RegisterDeviceRequestVM { Name = name, PublicKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) });

    /// <summary>Host side: opens the control channel and waits until the relay reports the device online.</summary>
    public async Task<WebSocket> ConnectControlAsync(string accountToken, DeviceRegisteredVM host)
    {
        var client = _factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + host.Token;
        var socket = await client.ConnectAsync(new Uri("ws://localhost" + TunnelProtocol.ControlPath), CancellationToken.None);

        var owner = NewApi(accountToken);
        var deadline = DateTime.UtcNow + Timeout;
        while (!(await owner.Devices.GetAllAsync()).Single(d => d.Id == host.Device.Id).IsOnline)
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Máy chủ chia sẻ không lên trạng thái online.");
            await Task.Delay(20);
        }
        return socket;
    }

    public async Task<TunnelSessionOpenedMessageVM> ReceiveControlMessageAsync(WebSocket control)
    {
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(Timeout);
        var result = await control.ReceiveAsync(buffer, cts.Token);
        Assert.AreEqual(WebSocketMessageType.Text, result.MessageType);
        Assert.IsTrue(result.EndOfMessage);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
        return JsonConvert.DeserializeObject<TunnelSessionOpenedMessageVM>(text, RelayJsonSettings.Create())!;
    }

    public Task<WebSocket> ConnectLaneAsync(Guid sessionId, string ticket, int lane)
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
}
