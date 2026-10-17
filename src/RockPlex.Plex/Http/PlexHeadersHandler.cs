using System.Net.Http.Headers;

namespace RockPlex.Plex.Http;

/// <summary>
/// Adds the <c>X-Plex-*</c> identity headers and <c>Accept: application/json</c> (Plex defaults to XML).
/// Adds the account token unless the request already carries one (server requests use the server token).
/// Tokens only ever travel in headers, never in URLs, so logged URLs are clean.
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

        if (!headers.Contains(TokenHeader) && tokens.AccountToken is { Length: > 0 } token)
        {
            headers.TryAddWithoutValidation(TokenHeader, token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
