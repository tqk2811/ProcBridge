using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ProcBridge.Relay.Services.Common.Common.Errors;
using ProcBridge.Relay.Services.Tunnel;
using ProcBridge.Relay.Services.Tunnel.Models;

namespace ProcBridge.Relay.Tests.Unit;

/// <summary>Hub refusals that need no control channel; everything with a proven host runs end to end.</summary>
[TestClass]
public sealed class TunnelHubTests
{
    private static TunnelHub NewHub() => new(Options.Create(new TunnelOptions()), TimeProvider.System, NullLogger<TunnelHub>.Instance);

    [TestMethod]
    public async Task MáyChủOfflineThìMởPhiênBáoHostOffline()
    {
        using var hub = NewHub();
        var ex = await Assert.ThrowsExactlyAsync<ServiceException>(() => hub.OpenSessionAsync("AAAA-BBBB", "10.0.0.1", CancellationToken.None));
        Assert.AreEqual(RelayErrors.HostOffline, ex.Code);
        Assert.AreEqual(409, ex.StatusCode);
    }

    [TestMethod]
    public void PhiênKhôngTồnTạiThìKhôngGiữĐượcLane()
    {
        using var hub = NewHub();
        var ex = Assert.ThrowsExactly<ServiceException>(() => hub.ReserveLane(Guid.NewGuid(), "pbt_x", 0, "10.0.0.1"));
        Assert.AreEqual(RelayErrors.TunnelSessionNotFound, ex.Code);
    }

    [TestMethod]
    public void TrầnKênhĐiềuKhiểnTínhTheoĐịaChỉ()
    {
        using var hub = new TunnelHub(Options.Create(new TunnelOptions { MaxControlChannelsPerAddress = 0 }), TimeProvider.System, NullLogger<TunnelHub>.Instance);
        Assert.IsFalse(hub.CanOpenControlChannel("10.0.0.1"));
    }
}
