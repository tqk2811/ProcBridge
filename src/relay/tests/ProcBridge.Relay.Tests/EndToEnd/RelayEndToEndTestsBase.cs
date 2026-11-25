using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json;

using ProcBridge.Crypto.Identity;
using ProcBridge.Crypto.Noise;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Json;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>
/// End-to-end scenarios through the in-process relay (REST + WebSocket). The relay has no accounts: a host proves its
/// identity key on the control channel, a client opens a session by HostId (derived class supplies the host).
/// </summary>
public abstract class RelayEndToEndTestsBase
{
    protected abstract RelayApiFactory Factory { get; }

    private RelayTestClient Relay => new(Factory);

    [TestMethod]
    public async Task HaiMáyGhépPhiênVàChuyểnTiếpHaiChiềuTrênNhiềuLane()
    {
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await Relay.ConnectControlAsync(hostKey);
        Assert.AreEqual(hostKey.Fingerprint, hostId, "HostId là vân tay của khoá đã chứng minh.");
        Assert.IsTrue(Relay.IsOnline(hostId));

        var session = await Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
        var opened = await RelayTestClient.ReceiveSessionOpenedAsync(control);
        Assert.AreEqual(session.SessionId, opened.SessionId);
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

        // Closing one end closes the other end of the same lane, and only that lane.
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
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await Relay.ConnectControlAsync(hostKey);
        var session = await Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
        var opened = await RelayTestClient.ReceiveSessionOpenedAsync(control);

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
    public async Task KhôngChiếmĐượcHostIdCủaMáyKhác()
    {
        using var victimKey = IdentityKey.Generate();
        using var attackerKey = IdentityKey.Generate();
        var (victim, victimId) = await Relay.ConnectControlAsync(victimKey);

        // The attacker proves only its own key: the relay registers it under its own fingerprint, never the victim's.
        var (attacker, attackerId) = await Relay.ConnectControlAsync(attackerKey);
        Assert.AreNotEqual(victimId, attackerId);
        Assert.IsTrue(Relay.IsOnline(victimId), "Kênh của máy thật vẫn còn.");

        // A session to the victim's HostId reaches the victim, not the attacker.
        var session = await Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = victimId });
        Assert.AreEqual(session.SessionId, (await RelayTestClient.ReceiveSessionOpenedAsync(victim)).SessionId);

        // Knowing only the victim's PUBLIC key does not help: the handshake needs the private key, the relay drops it.
        var forged = await Relay.OpenControlSocketAsync();
        using (var fake = IdentityKey.Generate())
        using (var handshake = NoiseHandshake.CreatePairing(isInitiator: true, fake, Encoding.ASCII.GetBytes(TunnelProtocol.ControlPrologue)))
        {
            await forged.SendAsync(handshake.WriteMessage([]), WebSocketMessageType.Binary, true, CancellationToken.None);
            await RelayTestClient.ReceiveAsync(forged);
            // Garbage in place of message 3 (no static key the relay can authenticate).
            await forged.SendAsync(RandomNumberGenerator.GetBytes(96), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        await RelayTestClient.AssertClosedAsync(forged);
        Assert.IsTrue(Relay.IsOnline(victimId));
        victim.Abort();
        attacker.Abort();
    }

    [TestMethod]
    public async Task KênhĐiềuKhiểnKhôngBắtTayThìKhôngĐượcĐăngKý()
    {
        var socket = await Relay.OpenControlSocketAsync();
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"hello\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await RelayTestClient.AssertClosedAsync(socket);
    }

    [TestMethod]
    public async Task KênhMớiCủaCùngMáyThayKênhCũ()
    {
        using var hostKey = IdentityKey.Generate();
        var (first, hostId) = await Relay.ConnectControlAsync(hostKey);
        var (second, sameId) = await Relay.ConnectControlAsync(hostKey);
        Assert.AreEqual(hostId, sameId);
        await RelayTestClient.AssertClosedAsync(first);
        var session = await Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
        Assert.AreEqual(session.SessionId, (await RelayTestClient.ReceiveSessionOpenedAsync(second)).SessionId);
        second.Abort();
    }

    [TestMethod]
    public async Task SaiVéHoặcLaneBậnKhôngMởĐượcLane()
    {
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await Relay.ConnectControlAsync(hostKey);
        var session = await Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
        await RelayTestClient.ReceiveSessionOpenedAsync(control);

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
        using var hostKey = IdentityKey.Generate();
        var offline = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostKey.Fingerprint }));
        Assert.AreEqual(409, offline.StatusCode);
        Assert.AreEqual("HOST_OFFLINE", offline.Code);

        var junk = await Assert.ThrowsExactlyAsync<RelayApiException>(
            () => Relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = new string('A', 200) }));
        Assert.AreEqual(400, junk.StatusCode);
    }
}
