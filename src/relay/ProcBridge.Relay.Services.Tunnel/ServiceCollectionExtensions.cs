using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Tunnel.Interfaces;

namespace ProcBridge.Relay.Services.Tunnel;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTunnelServices(this IServiceCollection services)
    {
        // Singleton: the hub IS the in-memory registry of live control channels and sessions; every request must
        // see the same one.
        services.AddSingleton<TunnelHub>();
        services.AddSingleton<ITunnelHub>(sp => sp.GetRequiredService<TunnelHub>());
        services.AddScoped<ITunnelSessionService, TunnelSessionService>();
        return services;
    }
}
