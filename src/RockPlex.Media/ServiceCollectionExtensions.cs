using Microsoft.Extensions.DependencyInjection;
using RockPlex.Media.Art;
using RockPlex.Media.Ffmpeg;
using RockPlex.Device.Sync;
using RockPlex.Media.Pipeline;
using RockPlex.Media.Sync;

namespace RockPlex.Media;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the media pipeline, with its caches under <paramref name="cacheRoot"/>.</summary>
    public static IServiceCollection AddRockPlexMedia(this IServiceCollection services, string cacheRoot)
    {
        services.AddSingleton(new MediaPaths(cacheRoot));
        services.AddSingleton<FfmpegRunner>();
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<FlacConverter>();
        services.AddSingleton<ContainerCleaner>();
        services.AddSingleton<AudioVerifier>();
        services.AddSingleton<ConversionCache>();
        services.AddSingleton<AlbumArtProvider>();
        services.AddSingleton<TrackPipeline>();
        services.AddSingleton<TestExportService>();
        services.AddSingleton<DeviceFileOps>();
        services.AddSingleton<SyncEngine>();
        return services;
    }
}
