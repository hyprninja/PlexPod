using Microsoft.Extensions.Logging;
using RockPlex.Plex.Auth;
using RockPlex.Plex.Dto;
using RockPlex.Plex.Http;
using RockPlex.Plex.Library;

namespace RockPlex.Plex.Servers;

public sealed record PlexConnection(Uri? Uri, string? Address, int Port, string Protocol, bool Local, bool Relay);

public sealed record PlexServer(
    string Name,
    string MachineIdentifier,
    string AccessToken,
    bool Owned,
    IReadOnlyList<PlexConnection> Connections);

/// <summary>A reachable server: base URL plus the server's own access token.</summary>
public sealed record PlexServerConnection(Uri BaseUri, string AccessToken, string MachineIdentifier, string Name);

public interface IPlexServerDiscovery
{
    Task<IReadOnlyList<PlexServer>> GetServersAsync(CancellationToken ct);

    /// <summary>
    /// Probes candidates in order and returns the best reachable one, or null if none answers.
    /// A reachable <paramref name="overrideUrl"/> always wins. Relay connections are never used.
    /// </summary>
    Task<PlexServerConnection?> SelectConnectionAsync(PlexServer server, Uri? overrideUrl, CancellationToken ct);
}

public sealed class PlexServerDiscovery(
    IHttpClientFactory httpFactory,
    PlexClientOptions options,
    ILogger<PlexServerDiscovery> logger) : IPlexServerDiscovery
{
    public async Task<IReadOnlyList<PlexServer>> GetServersAsync(CancellationToken ct)
    {
        var http = httpFactory.CreateClient(PlexAuthService.HttpClientName);
        using var response = await http.GetAsync("api/v2/resources?includeHttps=1", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var resources = await PlexJson.ReadAsync<List<ResourceDto>>(response, ct).ConfigureAwait(false);
        return ParseServers(resources);
    }

    internal static IReadOnlyList<PlexServer> ParseServers(IEnumerable<ResourceDto> resources) =>
        resources
            .Where(r => (r.Provides ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains("server", StringComparer.OrdinalIgnoreCase))
            .Where(r => !string.IsNullOrEmpty(r.ClientIdentifier))
            .Select(r => new PlexServer(
                r.Name ?? r.ClientIdentifier!,
                r.ClientIdentifier!,
                r.AccessToken ?? string.Empty,
                r.Owned,
                (r.Connections ?? [])
                    .Select(c => new PlexConnection(
                        Uri.TryCreate(c.Uri, UriKind.Absolute, out var u) ? u : null,
                        c.Address,
                        c.Port,
                        c.Protocol ?? "http",
                        c.Local,
                        c.Relay))
                    .ToList()))
            .ToList();

    /// <summary>
    /// Probe order: override, local uris (usually https plex.direct), local http://address:port
    /// (plex.direct DNS can be blocked by routers), then remote non-relay. Relay is excluded.
    /// </summary>
    public static IReadOnlyList<Uri> OrderCandidates(PlexServer server, Uri? overrideUrl)
    {
        ArgumentNullException.ThrowIfNull(server);
        var result = new List<Uri>();
        void Add(Uri? uri)
        {
            if (uri is not null && !result.Contains(uri))
            {
                result.Add(uri);
            }
        }

        Add(overrideUrl);
        var direct = server.Connections.Where(c => !c.Relay).ToList();
        foreach (var c in direct.Where(c => c.Local).OrderBy(c => c.Protocol == "https" ? 0 : 1))
        {
            Add(c.Uri);
        }

        foreach (var c in direct.Where(c => c.Local))
        {
            Add(PlainHttp(c));
        }

        foreach (var c in direct.Where(c => !c.Local))
        {
            Add(c.Uri);
        }

        return result;
    }

    public async Task<PlexServerConnection?> SelectConnectionAsync(PlexServer server, Uri? overrideUrl, CancellationToken ct)
    {
        var candidates = OrderCandidates(server, overrideUrl);

        // Probe all at once, then take the best-ranked success.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var probes = candidates.Select(uri => ProbeAsync(uri, server.AccessToken, cts.Token)).ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (await probes[i].ConfigureAwait(false))
            {
                await cts.CancelAsync().ConfigureAwait(false);
                logger.LogInformation("Using {Server} at {Uri}", server.Name, candidates[i]);
                return new PlexServerConnection(candidates[i], server.AccessToken, server.MachineIdentifier, server.Name);
            }
        }

        ct.ThrowIfCancellationRequested();
        logger.LogWarning("No reachable connection for {Server} ({Count} tried)", server.Name, candidates.Count);
        return null;
    }

    private async Task<bool> ProbeAsync(Uri baseUri, string token, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.ProbeTimeout);
        try
        {
            var http = httpFactory.CreateClient(PlexLibraryClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "identity"));
            if (token.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(PlexHeadersHandler.TokenHeader, token);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            logger.LogDebug("Probe {Uri} → {Status}", baseUri, (int)response.StatusCode);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            logger.LogDebug("Probe {Uri} failed: {Message}", baseUri, ex.Message);
            return false;
        }
    }

    private static Uri? PlainHttp(PlexConnection c)
    {
        if (string.IsNullOrEmpty(c.Address) || c.Port <= 0)
        {
            return null;
        }

        return new UriBuilder(Uri.UriSchemeHttp, c.Address, c.Port).Uri;
    }
}
