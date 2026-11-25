using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
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
        using var hostKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        var session = await relay.NewApi().Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
        control.Abort();
        return (relay, session);
    }
}
