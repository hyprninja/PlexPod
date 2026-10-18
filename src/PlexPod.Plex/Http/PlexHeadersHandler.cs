using System.Net.Http.Headers;

namespace PlexPod.Plex.Http;

/// <summary>
/// Adds the <c>X-Plex-*</c> identity headers and <c>Accept: application/json</c> (Plex defaults to XML).
/// Adds the <b>account</b> token only to requests for plex.tv itself (HTTPS) that don't carry a token;
/// servers only ever see their own access token. Tokens travel in headers, never in URLs.
/// </summary>
public sealed class PlexHeadersHandler(PlexClientOptions options, IPlexTokenProvider tokens) : DelegatingHandler
{
    public const string TokenHeader = "X-Plex-Token";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers;
        headers.TryAddWithoutValidation("X-Plex-Product", options.Product);
        headers.TryAddWithoutValidation("X-Plex-Version", options.Version);
        headers.TryAddWithoutValidation("X-Plex-Client-Identifier", options.ClientIdentifier);
        headers.TryAddWithoutValidation("X-Plex-Platform", options.Platform);
        headers.TryAddWithoutValidation("X-Plex-Device-Name", options.DeviceName);
        if (headers.Accept.Count == 0)
        {
            headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!headers.Contains(TokenHeader) && IsPlexTv(request.RequestUri) && tokens.AccountToken is { Length: > 0 } token)
        {
            headers.TryAddWithoutValidation(TokenHeader, token);
        }

        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>plex.tv or a subdomain of it, over HTTPS.</summary>
    internal static bool IsPlexTv(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("plex.tv", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".plex.tv", StringComparison.OrdinalIgnoreCase));
}
