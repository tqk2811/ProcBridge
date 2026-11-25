using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Common.Rules;
using ProcBridge.Relay.Services.Common.Security;

namespace ProcBridge.Relay.Services.Common;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCommonServices(this IServiceCollection services)
    {
        // Stateless, thread-safe: one instance for the process.
        services.AddSingleton<PasswordHasher>();
        services.AddScoped<ShareAccessService>();
        return services;
    }
}
