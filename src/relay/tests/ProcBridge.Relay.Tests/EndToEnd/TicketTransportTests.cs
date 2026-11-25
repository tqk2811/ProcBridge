using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>The lane ticket travels in the Authorization header; in the URL only for old peers, behind a switch.</summary>
[TestClass]
public sealed class TicketTransportTests
{
    [TestMethod]
    public async Task VéTrongUrlBịTừChốiMặcĐịnhVàChỉNhậnKhiBậtCờ()
    {
        await using var strict = new RelayApiFactory();
        var (relay, session) = await OpenSessionAsync(strict);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => relay.ConnectLaneWithTicketInQueryAsync(session.SessionId, session.Ticket, 0));
        StringAssert.Contains(refused.Message, "404");
        (await relay.ConnectLaneAsync(session.SessionId, session.Ticket, 0)).Abort();

        await using var legacy = new RelayApiFactory(new Dictionary<string, string?> { ["Relay:Tunnel:AllowTicketInQuery"] = "true" });
        var (legacyRelay, legacySession) = await OpenSessionAsync(legacy);
        (await legacyRelay.ConnectLaneWithTicketInQueryAsync(legacySession.SessionId, legacySession.Ticket, 0)).Abort();
    }

    private static async Task<(RelayTestClient Relay, TunnelSessionVM Session)> OpenSessionAsync(RelayApiFactory factory)
    {
        var relay = new RelayTestClient(factory);
        var owner = await relay.NewAccountAsync("chu");
        var host = await relay.NewDeviceAsync(owner, "may-b");
        var client = await relay.NewDeviceAsync(owner, "may-a");
        var share = await relay.NewApi(owner).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "src" });
        await relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = client.Device.Id });
        var control = await relay.ConnectControlAsync(owner, host);
        var session = await relay.NewApi(client.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        control.Abort();
        return (relay, session);
    }
}
