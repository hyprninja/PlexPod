using Microsoft.Extensions.DependencyInjection;
using PlexPod.Media.Art;
using PlexPod.Media.Ffmpeg;
using PlexPod.Device.Sync;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Stats;
using PlexPod.Media.Sync;

namespace PlexPod.Media;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the media pipeline, with its caches under <paramref name="cacheRoot"/>.</summary>
    public static IServiceCollection AddPlexPodMedia(this IServiceCollection services, string cacheRoot)
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
        services.AddSingleton<StatsService>();
        services.AddSingleton<SyncEngine>();
        services.AddSingleton<OperationGate>();
        return services;
    }
}
