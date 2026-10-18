using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using PlexPod.Plex.Auth;
using PlexPod.Plex.Http;
using PlexPod.Plex.Library;
using PlexPod.Plex.Servers;

namespace PlexPod.Plex;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Plex services. The caller registers <see cref="PlexClientOptions"/> and may replace
    /// <see cref="IPlexTokenProvider"/> (the default is <see cref="InMemoryPlexTokenProvider"/>).
    /// </summary>
    public static IServiceCollection AddPlexPodPlex(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryPlexTokenProvider>();
        services.TryAddSingleton<IPlexTokenProvider>(sp => sp.GetRequiredService<InMemoryPlexTokenProvider>());
        services.AddTransient<PlexHeadersHandler>();
        services.AddTransient<TransientRetryHandler>();

        services.AddHttpClient<IPlexAuthService, PlexAuthService>(PlexAuthService.HttpClientName, http =>
            {
                http.BaseAddress = PlexAuthService.BaseAddress;
                http.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<PlexHeadersHandler>()
            .AddHttpMessageHandler<TransientRetryHandler>()
            .RedactLoggedHeaders(_ => true);

        // No overall timeout: original-file downloads can take minutes. Metadata calls set their own.
        services.AddHttpClient(PlexLibraryClient.HttpClientName, http => http.Timeout = Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler<PlexHeadersHandler>()
            .AddHttpMessageHandler<TransientRetryHandler>()
            .RedactLoggedHeaders(_ => true);

        services.AddSingleton<IPlexServerDiscovery, PlexServerDiscovery>();
        services.AddSingleton<IPlexLibraryClientFactory, PlexLibraryClientFactory>();
        services.AddSingleton<TrackDetailEnricher>();
        return services;
    }
}
