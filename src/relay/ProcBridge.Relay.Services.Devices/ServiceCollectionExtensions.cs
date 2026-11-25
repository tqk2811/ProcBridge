using Microsoft.Extensions.DependencyInjection;

using ProcBridge.Relay.Services.Devices.Interfaces;

namespace ProcBridge.Relay.Services.Devices;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDevicesServices(this IServiceCollection services)
    {
        services.AddScoped<IDeviceService, DeviceService>();
        return services;
    }
}
