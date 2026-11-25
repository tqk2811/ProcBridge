using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Accounts.Interfaces;

namespace ProcBridge.Relay.Services.Accounts;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAccountsServices(this IServiceCollection services)
    {
        services.AddScoped<IAccountService, AccountService>();
        return services;
    }
}
