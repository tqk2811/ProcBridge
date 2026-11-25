using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Common.Presence.Interfaces;
using ProcBridge.Relay.Services.Tunnel.Interfaces;

namespace ProcBridge.Relay.Services.Tunnel;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTunnelServices(this IServiceCollection services)
    {
        // Singleton: the hub IS the in-memory registry of live control channels and sessions; every request must
        // see the same one. Presence is the same object seen from the Devices/Shares side.
        services.AddSingleton<TunnelHub>();
        services.AddSingleton<ITunnelHub>(sp => sp.GetRequiredService<TunnelHub>());
        services.AddSingleton<IDevicePresence>(sp => sp.GetRequiredService<TunnelHub>());
        services.AddSingleton<ITunnelTerminator>(sp => sp.GetRequiredService<TunnelHub>());
        services.AddScoped<ITunnelSessionService, TunnelSessionService>();
        return services;
    }
}
