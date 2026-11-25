using ProcBridge.Crypto.Identity;
using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>Abuse limits and forwarded headers, each on its own in-process relay.</summary>
[TestClass]
public sealed class RelayHardeningTests
{
    private const string TestIp = TestRemoteAddressStartupFilter.HeaderName;

    private static IRelayApi ApiFrom(RelayApiFactory factory, string remoteIp, string? forwardedFor = null)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(TestIp, remoteIp);
        if (forwardedFor is not null)
            http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        return new RelayApiImplement(http);
    }

    private static async Task<int> OpenStatusAsync(IRelayApi api, string hostId)
    {
        try
        {
            await api.Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { HostId = hostId });
            return 200;
        }
        catch (RelayApiException e)
        {
            return e.StatusCode;
        }
    }

    private static Dictionary<string, string?> SessionRateLimit(int permits) => new()
    {
        ["Relay:RateLimit:TunnelSessionPermitLimit"] = permits.ToString(),
        ["Relay:RateLimit:Window"] = "00:10:00",
    };

    [TestMethod]
    public async Task MởPhiênBịGiớiHạnTheoĐịaChỉ()
    {
        await using var factory = new RelayApiFactory(SessionRateLimit(2));
        // Offline host: 409, but every attempt still counts against the address.
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.1.0.1"), "AAAA"));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.1.0.1"), "AAAA"));
        Assert.AreEqual(429, await OpenStatusAsync(ApiFrom(factory, "10.1.0.1"), "AAAA"));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.1.0.2"), "AAAA"), "Địa chỉ khác có hạn mức riêng.");
    }

    [TestMethod]
    public async Task MặcĐịnhKhôngTinHeaderXForwardedFor()
    {
        await using var factory = new RelayApiFactory(SessionRateLimit(2));
        // One peer that rotates X-Forwarded-For to dodge the per-address limit.
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.1"), "A"));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.2"), "A"));
        Assert.AreEqual(429, await OpenStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.3"), "A"));
    }

    [TestMethod]
    public async Task ProxyĐượcCấuHìnhThìHạnMứcTínhTheoĐịaChỉKháchThật()
    {
        var settings = SessionRateLimit(2);
        settings["Relay:ForwardedHeaders:KnownProxies:0"] = "10.0.0.1";
        await using var factory = new RelayApiFactory(settings);

        // Three different real clients behind the trusted proxy: each has its own budget.
        for (var i = 1; i <= 3; i++)
            Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.0.0.1", $"203.0.113.{i}"), "A"));
        // The same real client keeps hitting its own limit.
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "A"));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "A"));
        Assert.AreEqual(429, await OpenStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "A"));
        // A peer that is NOT the listed proxy is not believed: its header is ignored.
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.60"), "A"));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.61"), "A"));
        Assert.AreEqual(429, await OpenStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.62"), "A"));
    }

    [TestMethod]
    public async Task SốPhiênSốngMỗiHostIdVàMỗiĐịaChỉCóTrần()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:Tunnel:MaxSessionsPerHost"] = "2",
            ["Relay:Tunnel:MaxSessionsPerAddress"] = "2",
        });
        var relay = new RelayTestClient(factory);
        using var hostKey = IdentityKey.Generate();
        using var otherKey = IdentityKey.Generate();
        using var thirdKey = IdentityKey.Generate();
        var (control, hostId) = await relay.ConnectControlAsync(hostKey);
        var (other, otherId) = await relay.ConnectControlAsync(otherKey);
        var (third, thirdId) = await relay.ConnectControlAsync(thirdKey);

        Assert.AreEqual(200, await OpenStatusAsync(ApiFrom(factory, "10.2.0.1"), hostId));
        Assert.AreEqual(200, await OpenStatusAsync(ApiFrom(factory, "10.2.0.2"), hostId));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.2.0.3"), hostId), "Trần phiên của một HostId.");

        Assert.AreEqual(200, await OpenStatusAsync(ApiFrom(factory, "10.2.0.1"), otherId));
        Assert.AreEqual(409, await OpenStatusAsync(ApiFrom(factory, "10.2.0.1"), thirdId), "Trần phiên của một địa chỉ.");
        Assert.AreEqual(200, await OpenStatusAsync(ApiFrom(factory, "10.2.0.4"), thirdId));
        control.Abort();
        other.Abort();
        third.Abort();
    }

    [TestMethod]
    public async Task SốKênhĐiềuKhiểnMỗiĐịaChỉCóTrần()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?> { ["Relay:Tunnel:MaxControlChannelsPerAddress"] = "1" });
        var relay = new RelayTestClient(factory);
        using var key = IdentityKey.Generate();
        var (control, _) = await relay.ConnectControlAsync(key, "10.3.0.1");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => relay.OpenControlSocketAsync("10.3.0.1"));
        StringAssert.Contains(refused.Message, "429");
        using var other = IdentityKey.Generate();
        var (fromElsewhere, _) = await relay.ConnectControlAsync(other, "10.3.0.2");
        control.Abort();
        fromElsewhere.Abort();
    }
}
