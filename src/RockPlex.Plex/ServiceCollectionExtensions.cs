using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using RockPlex.Plex.Auth;
using RockPlex.Plex.Http;
using RockPlex.Plex.Library;
using RockPlex.Plex.Servers;

namespace RockPlex.Plex;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Plex services. The caller registers <see cref="PlexClientOptions"/> and may replace
    /// <see cref="IPlexTokenProvider"/> (the default is <see cref="InMemoryPlexTokenProvider"/>).
    /// </summary>
    public static IServiceCollection AddRockPlexPlex(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryPlexTokenProvider>();
        services.TryAddSingleton<IPlexTokenProvider>(sp => sp.GetRequiredService<InMemoryPlexTokenProvider>());
        services.AddTransient<PlexHeadersHandler>();

        services.AddHttpClient<IPlexAuthService, PlexAuthService>(PlexAuthService.HttpClientName, http =>
            {
                http.BaseAddress = PlexAuthService.BaseAddress;
                http.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<PlexHeadersHandler>()
            .RedactLoggedHeaders(_ => true);

        services.AddHttpClient(PlexLibraryClient.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(100))
            .AddHttpMessageHandler<PlexHeadersHandler>()
            .RedactLoggedHeaders(_ => true);

        services.AddSingleton<IPlexServerDiscovery, PlexServerDiscovery>();
        services.AddSingleton<IPlexLibraryClientFactory, PlexLibraryClientFactory>();
        services.AddSingleton<TrackDetailEnricher>();
        return services;
    }
}
