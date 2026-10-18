using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RockPlex.Plex.Http;

/// <summary>
/// Retries GET/HEAD requests whose connection dropped before a response arrived (e.g. a pooled
/// keep-alive connection reset by plex.tv). Non-idempotent requests (creating a PIN) and HTTP error
/// statuses are never retried, and neither is a caller's cancellation.
/// </summary>
public sealed class TransientRetryHandler(ILogger<TransientRetryHandler> logger) : DelegatingHandler
{
    public const int MaxRetries = 2;

    /// <summary>Delay before retry n is n × this.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (idempotent && attempt < MaxRetries && IsTransient(ex) && !cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Retrying {Method} {Uri} after a dropped connection ({Message})",
                    request.Method, PlexRedactor.Redact(request.RequestUri), ex.InnerException?.Message ?? ex.Message);
                await Task.Delay(BaseDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A connection that dropped mid-request: reset, aborted, or the response ended early. Refused
    /// connections, DNS failures and certificate errors are not transient (connection probes rely on
    /// failing fast on those).
    /// </summary>
    internal static bool IsTransient(HttpRequestException ex)
    {
        if (ex.StatusCode is not null
            || ex.HttpRequestError is HttpRequestError.SecureConnectionError or HttpRequestError.NameResolutionError)
        {
            return false;
        }

        if (ex.HttpRequestError is HttpRequestError.ResponseEnded)
        {
            return true;
        }

        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case System.Security.Authentication.AuthenticationException:
                    return false;
                case SocketException socket:
                    return socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown;
            }
        }

        // "Unable to read data from the transport connection" without a socket error code.
        return ex.InnerException is IOException;
    }
}
