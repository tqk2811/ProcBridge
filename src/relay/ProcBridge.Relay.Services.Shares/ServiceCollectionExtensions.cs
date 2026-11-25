using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Shares.Interfaces;

namespace ProcBridge.Relay.Services.Shares;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSharesServices(this IServiceCollection services)
    {
        services.AddScoped<IShareService, ShareService>();
        return services;
    }
}
