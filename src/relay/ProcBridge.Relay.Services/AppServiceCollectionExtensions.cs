using System.Reflection;

using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Accounts;
using ProcBridge.Relay.Services.Common;
using ProcBridge.Relay.Services.Devices;
using ProcBridge.Relay.Services.Shares;
using ProcBridge.Relay.Services.Tunnel;

namespace ProcBridge.Relay.Services;

/// <summary>Facade: registers every service feature of the relay.</summary>
public static class AppServiceCollectionExtensions
{
    /// <summary>Every feature assembly. Adding a feature: add its marker HERE and its Add call BELOW (a test checks both).</summary>
    public static readonly Assembly[] FeatureAssemblies =
    [
        typeof(CommonAssemblyMarker).Assembly,
        typeof(AccountsAssemblyMarker).Assembly,
        typeof(DevicesAssemblyMarker).Assembly,
        typeof(SharesAssemblyMarker).Assembly,
        typeof(TunnelAssemblyMarker).Assembly,
    ];

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddCommonServices();
        services.AddAccountsServices();
        services.AddDevicesServices();
        services.AddSharesServices();
        services.AddTunnelServices();
        return services;
    }
}
