using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PlexPod.Device;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlexPodDevice(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IDriveSource, SystemDriveSource>();
        services.AddSingleton<IRockboxDeviceLocator, RockboxDeviceLocator>();
        services.AddSingleton<DeviceWatcher>();
        return services;
    }
}
