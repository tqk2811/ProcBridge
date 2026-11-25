using System.Net.WebSockets;
using System.Security.Cryptography;

using Newtonsoft.Json;

using ProcBridge.Relay.Cli;
using ProcBridge.Relay.Cli.Interfaces;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>
/// End-to-end scenarios through the in-process relay (REST + WebSocket) on its in-memory store (derived class).
/// </summary>
public abstract class RelayEndToEndTestsBase
{
    protected abstract RelayApiFactory Factory { get; }

    private RelayTestClient Relay => new(Factory);

    [TestMethod]
    public async Task HaiMáyGhépPhiênVàChuyểnTiếpHaiChiềuTrênNhiềuLane()
    {
        var (_, host, client, share, control) = await SetUpSharedHostAsync();

        var session = await Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        var opened = await Relay.ReceiveControlMessageAsync(control);
        Assert.AreEqual(session.SessionId, opened.SessionId);
        Assert.AreEqual(client.Device.Id, opened.ClientDeviceId);
        Assert.AreEqual(client.Device.PublicKey, opened.ClientPublicKey);
        Assert.AreEqual(host.Device.PublicKey, session.HostPublicKey);
        Assert.AreNotEqual(session.Ticket, opened.Ticket);

        // Two lanes in parallel (decision 15.1), opened in crossed order to prove pairing is per lane index.
        var clientLane0 = Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 0);
        var hostLane1 = Relay.ConnectLaneAsync(opened.SessionId, opened.Ticket, 1);
        var hostLane0 = Relay.ConnectLaneAsync(opened.SessionId, opened.Ticket, 0);
        var clientLane1 = Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 1);
        var c0 = await clientLane0; var h0 = await hostLane0; var c1 = await clientLane1; var h1 = await hostLane1;

        var big = RandomNumberGenerator.GetBytes(300 * 1024);
        var small = RandomNumberGenerator.GetBytes(17);
        await Task.WhenAll(RelayTestClient.SendAsync(c0, big), RelayTestClient.SendAsync(h1, small));
        CollectionAssert.AreEqual(big, await RelayTestClient.ReceiveAsync(h0));
        CollectionAssert.AreEqual(small, await RelayTestClient.ReceiveAsync(c1));

        var reply = RandomNumberGenerator.GetBytes(1024);
        await RelayTestClient.SendAsync(h0, reply);
        CollectionAssert.AreEqual(reply, await RelayTestClient.ReceiveAsync(c0));

        // Closing one end closes the other end of the same lane, and only that lane. The close handshake travels
        // end to end: the client's close reaches the host, the host's answer reaches the client.
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            await c0.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            var atHost = await h0.ReceiveAsync(new byte[16], cts.Token);
            Assert.AreEqual(WebSocketMessageType.Close, atHost.MessageType);
            Assert.AreEqual("bye", h0.CloseStatusDescription);
            await h0.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            var atClient = await c0.ReceiveAsync(new byte[16], cts.Token);
            Assert.AreEqual(WebSocketMessageType.Close, atClient.MessageType);
        }
        await RelayTestClient.SendAsync(c1, small);
        CollectionAssert.AreEqual(small, await RelayTestClient.ReceiveAsync(h1));
        c1.Abort();
        h1.Abort();
        control.Abort();
    }

    [TestMethod]
    public async Task LaneCủaMáyKháchĐượcBáoChoMáyChủQuaKênhĐiềuKhiển()
    {
        var (_, _, client, share, control) = await SetUpSharedHostAsync();
        var session = await Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        var opened = await Relay.ReceiveControlMessageAsync(control);

        var clientLane = Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 5);
        var request = JsonConvert.DeserializeObject<TunnelLaneRequestedMessageVM>(
            await RelayTestClient.ReceiveControlMessageAsync(control, TunnelProtocol.LaneRequestedMessage), RelayJsonSettings.Create())!;
        Assert.AreEqual(session.SessionId, request.SessionId);
        Assert.AreEqual(5, request.Lane);

        var hostLane = await Relay.ConnectLaneAsync(opened.SessionId, opened.Ticket, request.Lane);
        var c = await clientLane;
        var payload = RandomNumberGenerator.GetBytes(64);
        await RelayTestClient.SendAsync(c, payload);
        CollectionAssert.AreEqual(payload, await RelayTestClient.ReceiveAsync(hostLane));
        c.Abort();
        hostLane.Abort();
        control.Abort();
    }

    [TestMethod]
    public async Task ThuHồiQuyềnHoặcMáyCắtLaneĐangMở()
    {
        var (owner, host, client, share, control) = await SetUpSharedHostAsync();

        // Grant revoked: the open lane of that client is cut.
        var (c0, h0) = await OpenLaneAsync(client, share, control);
        await Relay.NewApi(owner).Shares.RevokeGrantAsync(share.Id, client.Device.Id);
        await AssertClosedAsync(c0);
        await AssertClosedAsync(h0);

        // Host device revoked: its control channel and lanes are cut.
        await Relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = client.Device.Id });
        var (c1, h1) = await OpenLaneAsync(client, share, control);
        await Relay.NewApi(owner).Devices.RevokeAsync(host.Device.Id);
        await AssertClosedAsync(c1);
        await AssertClosedAsync(h1);
        await AssertClosedAsync(control);
    }

    private async Task<(WebSocket Client, WebSocket Host)> OpenLaneAsync(ViewModels.Devices.DeviceRegisteredVM client, ShareVM share, WebSocket control)
    {
        var session = await Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        var opened = JsonConvert.DeserializeObject<TunnelSessionOpenedMessageVM>(
            await RelayTestClient.ReceiveControlMessageAsync(control, TunnelProtocol.SessionOpenedMessage), RelayJsonSettings.Create())!;
        var clientLane = Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 0);
        var hostLane = await Relay.ConnectLaneAsync(opened.SessionId, opened.Ticket, 0);
        var c = await clientLane;
        var payload = RandomNumberGenerator.GetBytes(16);
        await RelayTestClient.SendAsync(c, payload);
        CollectionAssert.AreEqual(payload, await RelayTestClient.ReceiveAsync(hostLane));
        return (c, hostLane);
    }

    /// <summary>The next receive ends (close frame or broken socket) instead of waiting.</summary>
    private static async Task AssertClosedAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            Assert.Fail("Kết nối không bị cắt sau khi thu hồi.");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    [TestMethod]
    public async Task MáyKhôngĐượcCấpQuyềnBịTừChốiMởPhiên()
    {
        var (owner, _, client, share, control) = await SetUpSharedHostAsync();
        var stranger = await Relay.NewDeviceAsync(await Relay.NewAccountAsync("lavi"), "may-la");

        var denied = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(stranger.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id }));
        Assert.AreEqual(403, denied.StatusCode);
        Assert.AreEqual("SHARE_ACCESS_DENIED", denied.Code);

        // Unknown share id answers exactly the same (no probing of which ids exist).
        var unknown = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = Guid.NewGuid() }));
        Assert.AreEqual(403, unknown.StatusCode);

        // Revoking the grant takes the right away.
        await Relay.NewApi(owner).Shares.RevokeGrantAsync(share.Id, client.Device.Id);
        var revoked = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id }));
        Assert.AreEqual("SHARE_ACCESS_DENIED", revoked.Code);

        // An account session token is not a device token.
        var wrongKind = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(owner).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id }));
        Assert.AreEqual(403, wrongKind.StatusCode);
        control.Abort();
    }

    [TestMethod]
    public async Task SaiVéHoặcLaneBậnKhôngMởĐượcLane()
    {
        var (_, _, client, share, control) = await SetUpSharedHostAsync();
        var session = await Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        await Relay.ReceiveControlMessageAsync(control);

        var badTicket = await Assert.ThrowsAsync<InvalidOperationException>(() => Relay.ConnectLaneAsync(session.SessionId, "pbt_sai", 0));
        StringAssert.Contains(badTicket.Message, "404");
        var badSession = await Assert.ThrowsAsync<InvalidOperationException>(() => Relay.ConnectLaneAsync(Guid.NewGuid(), session.Ticket, 0));
        StringAssert.Contains(badSession.Message, "404");

        var first = await Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 2);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => Relay.ConnectLaneAsync(session.SessionId, session.Ticket, 2));
        StringAssert.Contains(busy.Message, "409");
        first.Abort();
        control.Abort();
    }

    [TestMethod]
    public async Task MáyChiaSẻKhôngOnlineThìKhôngMởĐượcPhiên()
    {
        var owner = await Relay.NewAccountAsync("chu");
        var host = await Relay.NewDeviceAsync(owner, "may-b");
        var client = await Relay.NewDeviceAsync(owner, "may-a");
        var share = await Relay.NewApi(owner).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "src" });
        await Relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = client.Device.Id });

        var offline = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id }));
        Assert.AreEqual(409, offline.StatusCode);
        Assert.AreEqual("DEVICE_OFFLINE", offline.Code);
    }

    [TestMethod]
    public async Task TàiKhoảnKhácKhôngQuảnLýĐượcChiaSẻVàMáyCủaNgườiKhác()
    {
        var owner = await Relay.NewAccountAsync("chu");
        var other = await Relay.NewAccountAsync("khac");
        var host = await Relay.NewDeviceAsync(owner, "may-b");
        var otherDevice = await Relay.NewDeviceAsync(other, "may-khac");
        var share = await Relay.NewApi(owner).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "src" });

        var grant = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(other).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = otherDevice.Device.Id }));
        Assert.AreEqual(404, grant.StatusCode);
        var create = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi(other).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "chiem" }));
        Assert.AreEqual("DEVICE_NOT_FOUND", create.Code);
        var revoke = await Assert.ThrowsExactlyAsync<RelayApiException>(() => Relay.NewApi(other).Devices.RevokeAsync(host.Device.Id));
        Assert.AreEqual(404, revoke.StatusCode);
        Assert.IsEmpty(await Relay.NewApi(other).Shares.GetAllAsync());

        // Cross-account grant made by the real owner works, and shows up for the grantee device.
        await Relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = otherDevice.Device.Id });
        var accessible = await Relay.NewApi(otherDevice.Token).Devices.GetAccessibleSharesAsync();
        Assert.HasCount(1, accessible);
        Assert.AreEqual(share.Id, accessible[0].ShareId);
    }

    [TestMethod]
    public async Task ThiếtBịBịThuHồiThìTokenHếtDùng()
    {
        var owner = await Relay.NewAccountAsync("chu");
        var device = await Relay.NewDeviceAsync(owner, "may");
        await Relay.NewApi(owner).Devices.RevokeAsync(device.Device.Id);
        var ex = await Assert.ThrowsExactlyAsync<RelayApiException>(() => Relay.NewApi(device.Token).Devices.GetAccessibleSharesAsync());
        Assert.AreEqual(401, ex.StatusCode);
    }

    [TestMethod]
    public async Task CliQuảnLýTàiKhoảnMáyVàChiaSẻ()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "procbridge-relay-cli-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();
            var cli = new CliApp(output, error, new CliConfigStore(configPath), new FixedPassword("matkhau-dai-du"),
                _ => new RelayApiImplement(Factory.CreateClient()));
            var user = "cli" + Guid.NewGuid().ToString("N")[..8];
            const string url = "http://localhost/";

            Assert.AreEqual(0, await cli.RunAsync(["--url", url, "register", user]), error.ToString());
            Assert.AreEqual(0, await cli.RunAsync(["--url", url, "login", user]), error.ToString());
            Assert.AreEqual(0, await cli.RunAsync(["whoami"]), error.ToString());
            StringAssert.Contains(output.ToString(), user);

            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await cli.RunAsync(["device", "register", "may-b"]), error.ToString());
            var hostId = Line(output, "deviceId: ");
            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await cli.RunAsync(["device", "register", "may-a"]), error.ToString());
            var clientId = Line(output, "deviceId: ");
            var clientToken = Line(output, "token: ");

            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await cli.RunAsync(["share", "create", hostId, "nguon"]), error.ToString());
            var shareId = Line(output, "shareId: ");
            Assert.AreEqual(0, await cli.RunAsync(["share", "grant", shareId, clientId]), error.ToString());

            output.GetStringBuilder().Clear();
            Assert.AreEqual(0, await cli.RunAsync(["--token", clientToken, "device", "shares"]), error.ToString());
            StringAssert.Contains(output.ToString(), shareId);

            Assert.AreEqual(0, await cli.RunAsync(["share", "revoke", shareId, clientId]), error.ToString());
            Assert.AreEqual(1, await cli.RunAsync(["--token", clientToken, "tunnel", "open", shareId]));
            StringAssert.Contains(error.ToString(), "SHARE_ACCESS_DENIED");

            Assert.AreEqual(2, await cli.RunAsync(["share", "grant", "khong-phai-guid", clientId]));
            Assert.AreEqual(0, await cli.RunAsync(["logout"]), error.ToString());
            Assert.AreEqual(2, await cli.RunAsync(["whoami"]));
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    private async Task<(string OwnerToken, ViewModels.Devices.DeviceRegisteredVM Host, ViewModels.Devices.DeviceRegisteredVM Client, ShareVM Share, WebSocket Control)> SetUpSharedHostAsync()
    {
        var owner = await Relay.NewAccountAsync("chu");
        var host = await Relay.NewDeviceAsync(owner, "may-b");
        var client = await Relay.NewDeviceAsync(owner, "may-a");
        var share = await Relay.NewApi(owner).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "src" });
        await Relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = client.Device.Id });
        var control = await Relay.ConnectControlAsync(owner, host);
        return (owner, host, client, share, control);
    }

    private static string Line(StringWriter output, string prefix)
        => output.ToString().Split('\n').Select(x => x.Trim()).First(x => x.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    private sealed class FixedPassword(string password) : IPasswordReader
    {
        public string Read(string prompt) => password;
    }
}
