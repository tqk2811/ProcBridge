using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace ProcBridge.Relay.Testing.Infrastructure;

/// <summary>
/// In-process relay host. The relay keeps its data in memory, so every factory is a fresh, empty relay (plus whatever
/// <c>Relay:Accounts:Seed</c> in the settings creates).
/// </summary>
public sealed class RelayApiFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="settings">Extra configuration, applied after the test defaults (which allow self-registration and
    /// raise the sign-in rate limit, since tests create many accounts from one address).</param>
    public RelayApiFactory(IReadOnlyDictionary<string, string?>? settings = null)
        => _settings = settings ?? new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Relay:Accounts:AllowRegistration", "true");
        builder.UseSetting("Relay:RateLimit:AuthPermitLimit", "100000");
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
        builder.UseSetting("Relay:Tunnel:PairTimeout", "00:00:10");
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, TestRemoteAddressStartupFilter>());
    }
}
