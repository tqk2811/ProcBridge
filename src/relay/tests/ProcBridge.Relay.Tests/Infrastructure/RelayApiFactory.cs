using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using ProcBridge.Relay.Repositories.Abstractions.Interfaces;
using ProcBridge.Relay.Tests.Fakes;

namespace ProcBridge.Relay.Tests.Infrastructure;

/// <summary>
/// In-process relay host. With a connection string it runs the real EF repositories on PostgreSQL (and migrates on
/// start); without one it swaps in the in-memory repositories and skips migration, so the API and tunnel paths can be
/// exercised on any machine.
/// </summary>
public sealed class RelayApiFactory : WebApplicationFactory<Program>
{
    private readonly string? _connectionString;

    public RelayApiFactory(string? connectionString) => _connectionString = connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Relay", _connectionString ?? "Host=unused.invalid;Database=unused");
        builder.UseSetting("Relay:Database:MigrateOnStartup", _connectionString is null ? "false" : "true");
        builder.UseSetting("Relay:Tunnel:PairTimeout", "00:00:10");
        if (_connectionString is not null)
            return;

        builder.ConfigureServices(services =>
        {
            services.AddSingleton<InMemoryRelayStore>();
            services.RemoveAll<IAccountRepository>();
            services.RemoveAll<IAccountSessionRepository>();
            services.RemoveAll<IDeviceRepository>();
            services.RemoveAll<IShareRepository>();
            services.AddScoped<IAccountRepository, InMemoryAccountRepository>();
            services.AddScoped<IAccountSessionRepository, InMemoryAccountSessionRepository>();
            services.AddScoped<IDeviceRepository, InMemoryDeviceRepository>();
            services.AddScoped<IShareRepository, InMemoryShareRepository>();
        });
    }
}
