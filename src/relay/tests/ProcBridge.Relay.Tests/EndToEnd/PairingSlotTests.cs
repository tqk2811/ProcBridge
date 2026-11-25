using System.Net.WebSockets;

using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Shared.Tunnel;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>
/// Pairing slots: machine B opens one under the lookup key of its invite code, machine A finds it by the same key
/// (single use, short lifetime, rate limited) and gets a session to B plus B's HostId. Each test on its own relay.
/// </summary>
[TestClass]
public sealed class PairingSlotTests
{
    private static RelayApiFactory NewRelay(int pairingPermits = 100000, string? slotLifetime = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Relay:RateLimit:PairingPermitLimit"] = pairingPermits.ToString(),
            ["Relay:RateLimit:Window"] = "00:10:00",
        };
        if (slotLifetime is not null)
            settings["Relay:Tunnel:PairingSlotLifetime"] = slotLifetime;
        return new RelayApiFactory(settings);
    }

    private static string NewLookupKey() => RelayPairingCode.DeriveLookupKey(RelayPairingCode.Generate());

    private static async Task<int> OpenPairingStatusAsync(RelayTestClient relay, string lookupKey, string? remoteIp = null)
    {
        try
        {
            await relay.NewApi(remoteIp).Tunnel.OpenPairingSessionAsync(new OpenPairingSessionRequestVM { LookupKey = lookupKey });
            return 200;
        }
        catch (RelayApiException e)
        {
            return e.StatusCode;
        }
    }

    [TestMethod]
    public async Task MáyATìmThấyMáyBTheoMãMờiVàMởĐượcPhiên()
    {
        await using var factory = NewRelay();
        var relay = new RelayTestClient(factory);
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        string key = NewLookupKey();

        var registered = await RelayTestClient.RequestPairingSlotAsync(control, key);
        Assert.AreEqual(TunnelProtocol.PairingSlotRegisteredMessage, registered.Type);
        Assert.AreEqual(key, registered.LookupKey);
        Assert.IsNotNull(registered.ExpiresAt);
        Assert.IsTrue(relay.HasPairingSlot(hostId));

        var session = await relay.NewApi().Tunnel.OpenPairingSessionAsync(new OpenPairingSessionRequestVM { LookupKey = key });
        Assert.AreEqual(hostId, session.HostId, "Relay trả HostId của máy giữ chỗ để máy A so với vân tay bắt tay.");
        var opened = await RelayTestClient.ReceiveSessionOpenedAsync(control);
        Assert.AreEqual(session.SessionId, opened.SessionId);
        var claimed = Newtonsoft.Json.JsonConvert.DeserializeObject<TunnelPairingSlotStateMessageVM>(
            await RelayTestClient.ReceiveControlMessageAsync(control, TunnelProtocol.PairingSlotClaimedMessage),
            ProcBridge.Relay.Shared.Json.RelayJsonSettings.Create())!;
        Assert.AreEqual(key, claimed.LookupKey);
        Assert.AreEqual(session.SessionId, claimed.SessionId);

        // The session is an ordinary one: lanes pair and carry bytes.
        var clientLane = relay.ConnectLaneAsync(session.SessionId, session.Ticket, 0);
        var hostLane = await relay.ConnectLaneAsync(opened.SessionId, opened.Ticket, 0);
        var c = await clientLane;
        byte[] payload = [1, 2, 3];
        await RelayTestClient.SendAsync(c, payload);
        CollectionAssert.AreEqual(payload, await RelayTestClient.ReceiveAsync(hostLane));

        // Single use: the same code finds nothing the second time.
        Assert.IsFalse(relay.HasPairingSlot(hostId));
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, key));
        c.Abort();
        hostLane.Abort();
        control.Abort();
    }

    [TestMethod]
    public async Task SaiMãHoặcKhoáSaiDạngThìKhôngTìmThấy()
    {
        await using var factory = NewRelay();
        var relay = new RelayTestClient(factory);
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        await RelayTestClient.RequestPairingSlotAsync(control, NewLookupKey());

        var wrong = await Assert.ThrowsExactlyAsync<RelayApiException>(() => relay.NewApi().Tunnel.OpenPairingSessionAsync(
            new OpenPairingSessionRequestVM { LookupKey = NewLookupKey() }));
        Assert.AreEqual(404, wrong.StatusCode);
        Assert.AreEqual("PAIRING_SLOT_NOT_FOUND", wrong.Code);
        Assert.AreEqual(400, await OpenPairingStatusAsync(relay, "12345678"), "Mã trần (không phải khoá tra cứu) bị từ chối.");
        Assert.IsTrue(relay.HasPairingSlot(hostId), "Đoán sai không làm mất chỗ của máy B.");
        control.Abort();
    }

    [TestMethod]
    public async Task ChỗGhépCặpHếtHạnVàMấtTheoKênhĐiềuKhiển()
    {
        await using var factory = NewRelay(slotLifetime: "00:00:01");
        var relay = new RelayTestClient(factory);
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        string key = NewLookupKey();
        await RelayTestClient.RequestPairingSlotAsync(control, key);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, key), "Quá hạn thì chỗ biến mất.");

        await using var longer = NewRelay();
        relay = new RelayTestClient(longer);
        var (control2, hostId2) = await relay.ConnectControlAsync(hostKey);
        key = NewLookupKey();
        await RelayTestClient.RequestPairingSlotAsync(control2, key);
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await control2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        await RelayTestClient.AssertClosedAsync(control2);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (relay.IsOnline(hostId2) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsFalse(relay.HasPairingSlot(hostId2));
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, key), "Máy B rời relay thì chỗ ghép cặp mất theo.");
        control.Abort();
    }

    [TestMethod]
    public async Task HuỷHoặcThayChỗCũ()
    {
        await using var factory = NewRelay();
        var relay = new RelayTestClient(factory);
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        string first = NewLookupKey();
        string second = NewLookupKey();
        await RelayTestClient.RequestPairingSlotAsync(control, first);
        await RelayTestClient.RequestPairingSlotAsync(control, second);
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, first), "Mỗi máy chỉ một chỗ: chỗ mới thay chỗ cũ.");

        await RelayTestClient.SendControlAsync(control, new TunnelPairingSlotMessageVM(TunnelProtocol.PairingSlotCancelMessage, null));
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (relay.HasPairingSlot(hostId) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, second), "Huỷ thì chỗ biến mất.");
        control.Abort();
    }

    [TestMethod]
    public async Task KhôngChiếmĐượcKhoáTraCứuCủaMáyKhácVàKhoáSaiDạngBịTừChối()
    {
        await using var factory = NewRelay();
        var relay = new RelayTestClient(factory);
        using var victimKey = IdentityKey.Generate();
        using var otherKey = IdentityKey.Generate();
        var (victim, victimId) = await relay.ConnectControlAsync(victimKey);
        var (other, _) = await relay.ConnectControlAsync(otherKey);
        string key = NewLookupKey();
        await RelayTestClient.RequestPairingSlotAsync(victim, key);

        var clash = await RelayTestClient.RequestPairingSlotAsync(other, key);
        Assert.AreEqual(TunnelProtocol.PairingSlotRejectedMessage, clash.Type, "Trùng khoá thì máy đến sau phải chọn mã khác.");
        var junk = await RelayTestClient.RequestPairingSlotAsync(other, "not-a-key");
        Assert.AreEqual(TunnelProtocol.PairingSlotRejectedMessage, junk.Type);

        var session = await relay.NewApi().Tunnel.OpenPairingSessionAsync(new OpenPairingSessionRequestVM { LookupKey = key });
        Assert.AreEqual(victimId, session.HostId, "Chỗ vẫn thuộc máy đăng ký trước.");
        victim.Abort();
        other.Abort();
    }

    [TestMethod]
    public async Task TraCứuBịGiớiHạnTheoĐịaChỉ()
    {
        await using var factory = NewRelay(pairingPermits: 2);
        var relay = new RelayTestClient(factory);
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, NewLookupKey(), "10.2.0.1"));
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, NewLookupKey(), "10.2.0.1"));
        Assert.AreEqual(429, await OpenPairingStatusAsync(relay, NewLookupKey(), "10.2.0.1"));
        Assert.AreEqual(404, await OpenPairingStatusAsync(relay, NewLookupKey(), "10.2.0.2"), "Địa chỉ khác có hạn mức riêng.");
    }

    [TestMethod]
    public void MãMờiChuẩnHoáVàDẫnXuấtKhoáỔnĐịnh()
    {
        Assert.IsTrue(RelayPairingCode.TryNormalize(" 1234-5678 ", out string code));
        Assert.AreEqual("12345678", code);
        Assert.AreEqual("1234-5678", RelayPairingCode.Format(code));
        Assert.IsFalse(RelayPairingCode.TryNormalize("1234567", out _));
        Assert.IsFalse(RelayPairingCode.TryNormalize("1234567a", out _));
        Assert.IsFalse(RelayPairingCode.TryNormalize(null, out _));

        string key = RelayPairingCode.DeriveLookupKey(code);
        Assert.IsTrue(RelayPairingCode.IsLookupKey(key));
        Assert.AreEqual(key, RelayPairingCode.DeriveLookupKey("12345678"));
        Assert.AreNotEqual(key, RelayPairingCode.DeriveLookupKey("12345679"));
        Assert.IsFalse(key.Contains(code), "Khoá tra cứu không chứa mã.");

        string generated = RelayPairingCode.Generate();
        Assert.AreEqual(RelayPairingCode.Length, generated.Length);
        Assert.IsTrue(generated.All(char.IsAsciiDigit));
    }
}
