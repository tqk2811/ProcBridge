using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ProcBridge.Relay.Testing.Infrastructure;

/// <summary>In-process relay host. The relay keeps nothing but live channels in memory, so every factory is a fresh relay.</summary>
public sealed class RelayApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="settings">Extra configuration, applied after the test defaults (which raise the per-address limits,
    /// since every test client comes from the same address).</param>
    public RelayApiFactory(IReadOnlyDictionary<string, string?>? settings = null)
        => _settings = settings ?? new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Relay:RateLimit:TunnelSessionPermitLimit", "100000");
        builder.UseSetting("Relay:RateLimit:ControlPermitLimit", "100000");
        builder.UseSetting("Relay:RateLimit:LanePermitLimit", "100000");
        builder.UseSetting("Relay:Tunnel:MaxControlChannelsPerAddress", "100000");
        builder.UseSetting("Relay:Tunnel:MaxSessionsPerHost", "100000");
        builder.UseSetting("Relay:Tunnel:MaxSessionsPerAddress", "100000");
        builder.UseSetting("Relay:Tunnel:MaxLanesPerAddress", "100000");
        builder.UseSetting("Relay:Tunnel:PairTimeout", "00:00:10");
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestRemoteAddressStartupFilter>());
    }
}
