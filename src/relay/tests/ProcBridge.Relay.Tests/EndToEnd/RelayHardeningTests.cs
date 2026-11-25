using ProcBridge.Relay.Integration.Api.Implements;
using ProcBridge.Relay.Integration.Api.Interfaces;
using ProcBridge.Relay.Integration.Api.Models;
using ProcBridge.Relay.Testing.Infrastructure;
using ProcBridge.Relay.Tests.Infrastructure;
using ProcBridge.Relay.ViewModels.Accounts;
using ProcBridge.Relay.ViewModels.Shares;
using ProcBridge.Relay.ViewModels.Tunnel;

namespace ProcBridge.Relay.Tests.EndToEnd;

/// <summary>Abuse limits and forwarded headers, each on its own in-process relay (in-memory repositories).</summary>
[TestClass]
public sealed class RelayHardeningTests
{
    private const string TestIp = TestRemoteAddressStartupFilter.HeaderName;

    private static IRelayApi ApiFrom(RelayApiFactory factory, string remoteIp, string? forwardedFor = null, string? token = null)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add(TestIp, remoteIp);
        if (forwardedFor is not null)
            http.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);
        var api = new RelayApiImplement(http);
        api.SetBearerToken(token);
        return api;
    }

    private static async Task<int> LoginStatusAsync(IRelayApi api, string userName)
    {
        try
        {
            await api.Accounts.LoginAsync(new LoginRequestVM { UserName = userName, Password = "sai-mat-khau" });
            return 200;
        }
        catch (RelayApiException e)
        {
            return e.StatusCode;
        }
    }

    [TestMethod]
    public async Task ĐăngNhậpCùngTênTừNhiềuĐịaChỉVẫnBịChặnTheoTênĐăngNhập()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:RateLimit:LoginUserNamePermitLimit"] = "3",
            ["Relay:RateLimit:AuthWindow"] = "00:10:00",
        });

        for (var i = 0; i < 3; i++)
            Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, $"10.1.0.{i + 1}"), "nan-nhan"));
        Assert.AreEqual(429, await LoginStatusAsync(ApiFrom(factory, "10.1.0.99"), "nan-nhan"));
        Assert.AreEqual(429, await LoginStatusAsync(ApiFrom(factory, "10.1.0.100"), " NAN-NHAN "), "Cùng tên sau khi chuẩn hoá.");
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.1.0.101"), "nguoi-khac"), "Tên khác có hạn mức riêng.");
    }

    [TestMethod]
    public async Task MặcĐịnhKhôngTinHeaderXForwardedFor()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:RateLimit:AuthPermitLimit"] = "2",
            ["Relay:RateLimit:LoginUserNamePermitLimit"] = "1000",
            ["Relay:RateLimit:AuthWindow"] = "00:10:00",
        });

        // One peer that rotates X-Forwarded-For to dodge the per-address limit.
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.1"), "a"));
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.2"), "b"));
        Assert.AreEqual(429, await LoginStatusAsync(ApiFrom(factory, "10.9.9.9", "203.0.113.3"), "c"));
    }

    [TestMethod]
    public async Task ProxyĐượcCấuHìnhThìHạnMứcTínhTheoĐịaChỉKháchThật()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:ForwardedHeaders:KnownProxies:0"] = "10.0.0.1",
            ["Relay:RateLimit:AuthPermitLimit"] = "2",
            ["Relay:RateLimit:LoginUserNamePermitLimit"] = "1000",
            ["Relay:RateLimit:AuthWindow"] = "00:10:00",
        });

        // Three different real clients behind the trusted proxy: each has its own budget.
        for (var i = 1; i <= 3; i++)
            Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.0.0.1", $"203.0.113.{i}"), "a" + i));
        // The same real client keeps hitting its own limit.
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "x"));
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "y"));
        Assert.AreEqual(429, await LoginStatusAsync(ApiFrom(factory, "10.0.0.1", "203.0.113.50"), "z"));
        // A peer that is NOT the listed proxy is not believed: its header is ignored.
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.60"), "p"));
        Assert.AreEqual(401, await LoginStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.61"), "q"));
        Assert.AreEqual(429, await LoginStatusAsync(ApiFrom(factory, "10.0.0.2", "203.0.113.62"), "r"));
    }

    [TestMethod]
    public async Task MởPhiênTunnelBịGiớiHạnTheoMáy()
    {
        await using var factory = new RelayApiFactory(new Dictionary<string, string?>
        {
            ["Relay:RateLimit:TunnelSessionPermitLimit"] = "2",
            ["Relay:RateLimit:TunnelSessionWindow"] = "00:10:00",
        });
        var relay = new RelayTestClient(factory);
        var owner = await relay.NewAccountAsync("chu");
        var host = await relay.NewDeviceAsync(owner, "may-b");
        var clientA = await relay.NewDeviceAsync(owner, "may-a1");
        var clientB = await relay.NewDeviceAsync(owner, "may-a2");
        var share = await relay.NewApi(owner).Shares.CreateAsync(new CreateShareRequestVM { DeviceId = host.Device.Id, Name = "src" });
        await relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = clientA.Device.Id });
        await relay.NewApi(owner).Shares.GrantAsync(share.Id, new GrantShareRequestVM { DeviceId = clientB.Device.Id });
        using var control = await relay.ConnectControlAsync(owner, host);

        var a = relay.NewApi(clientA.Token);
        await a.Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        await a.Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        var blocked = await Assert.ThrowsExactlyAsync<RelayApiException>(() => a.Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id }));
        Assert.AreEqual(429, blocked.StatusCode);
        Assert.AreEqual("TOO_MANY_REQUESTS", blocked.Code);

        // Another device has its own budget.
        await relay.NewApi(clientB.Token).Tunnel.OpenSessionAsync(new OpenTunnelSessionRequestVM { ShareId = share.Id });
        control.Abort();
    }
}
