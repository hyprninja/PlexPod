using System.Net;
using Microsoft.Extensions.Logging;
using RockPlex.Plex.Dto;
using RockPlex.Plex.Http;

namespace RockPlex.Plex.Auth;

public sealed record PlexPin(long Id, string Code, DateTimeOffset ExpiresAt);

public enum TokenStatus
{
    Valid,

    /// <summary>plex.tv answered 401: the token is revoked and should be discarded.</summary>
    Invalid,

    /// <summary>Network or server trouble; the token may still be fine, so keep it.</summary>
    Unreachable,
}

public sealed record TokenValidation(TokenStatus Status, string? Username = null);

public sealed class PinExpiredException() : Exception("The Plex sign-in PIN expired before it was approved.");

public interface IPlexAuthService
{
    Task<PlexPin> CreatePinAsync(CancellationToken ct);

    string BuildAuthUrl(PlexPin pin);

    /// <summary>Polls until the PIN is approved; throws <see cref="PinExpiredException"/> when it expires.</summary>
    Task<string> PollForTokenAsync(PlexPin pin, CancellationToken ct);

    Task<TokenValidation> ValidateTokenAsync(string token, CancellationToken ct);
}

/// <summary>PIN sign-in against plex.tv (the legacy token flow; JWT auth is not in M1).</summary>
public sealed class PlexAuthService(
    HttpClient http,
    PlexClientOptions options,
    TimeProvider time,
    ILogger<PlexAuthService> logger) : IPlexAuthService
{
    public const string HttpClientName = "plex.tv";
    public static readonly Uri BaseAddress = new("https://plex.tv/");

    public async Task<PlexPin> CreatePinAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v2/pins?strong=true");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var pin = await PlexJson.ReadAsync<PinDto>(response, ct).ConfigureAwait(false);
        if (pin.Id == 0 || string.IsNullOrEmpty(pin.Code))
        {
            throw new PlexProtocolException("plex.tv returned a PIN without an id or code.");
        }

        var expires = pin.ExpiresAt
            ?? time.GetUtcNow().AddSeconds(pin.ExpiresIn is > 0 ? pin.ExpiresIn.Value : 1800);
        logger.LogInformation("Created Plex PIN {PinId}, expires {ExpiresAt:u}", pin.Id, expires);
        return new PlexPin(pin.Id, pin.Code, expires);
    }

    public string BuildAuthUrl(PlexPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return "https://app.plex.tv/auth#?clientID=" + Uri.EscapeDataString(options.ClientIdentifier)
            + "&code=" + Uri.EscapeDataString(pin.Code)
            + "&context%5Bdevice%5D%5Bproduct%5D=" + Uri.EscapeDataString(options.Product);
    }

    public async Task<string> PollForTokenAsync(PlexPin pin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pin);
        var path = $"api/v2/pins/{pin.Id}?code={Uri.EscapeDataString(pin.Code)}";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using (var response = await http.GetAsync(path, ct).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // plex.tv drops expired PINs.
                    throw new PinExpiredException();
                }

                response.EnsureSuccessStatusCode();
                var dto = await PlexJson.ReadAsync<PinDto>(response, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(dto.AuthToken))
                {
                    logger.LogInformation("Plex PIN {PinId} approved", pin.Id);
                    return dto.AuthToken;
                }
            }

            if (time.GetUtcNow() >= pin.ExpiresAt)
            {
                throw new PinExpiredException();
            }

            await Task.Delay(options.PinPollInterval, time, ct).ConfigureAwait(false);
        }
    }

    public async Task<TokenValidation> ValidateTokenAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/user");
        request.Headers.TryAddWithoutValidation(PlexHeadersHandler.TokenHeader, token);
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new TokenValidation(TokenStatus.Invalid);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("plex.tv user check returned {Status}", (int)response.StatusCode);
                return new TokenValidation(TokenStatus.Unreachable);
            }

            var user = await PlexJson.ReadAsync<UserDto>(response, ct).ConfigureAwait(false);
            return new TokenValidation(TokenStatus.Valid, user.Username ?? user.Title);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("plex.tv unreachable: {Message}", ex.Message);
            return new TokenValidation(TokenStatus.Unreachable);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("plex.tv user check timed out");
            return new TokenValidation(TokenStatus.Unreachable);
        }
    }
}
