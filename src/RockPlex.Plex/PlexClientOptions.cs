namespace RockPlex.Plex;

/// <summary>Identity sent to Plex in the <c>X-Plex-*</c> headers on every request.</summary>
public sealed class PlexClientOptions
{
    public string Product { get; init; } = "RockPlex";

    public string Version { get; init; } = "0.1.0";

    /// <summary>Stable GUID per install, created on first run and saved in settings.</summary>
    public required string ClientIdentifier { get; init; }

    public string DeviceName { get; init; } = Environment.MachineName;

    public string Platform { get; init; } = "Windows";

    /// <summary>How often the PIN is polled during sign-in.</summary>
    public TimeSpan PinPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Timeout for each <c>/identity</c> connection probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Page size for <c>X-Plex-Container-Size</c>.</summary>
    public int PageSize { get; init; } = 1000;
}

/// <summary>Supplies the plex.tv account token. Server requests carry the server's own access token.</summary>
public interface IPlexTokenProvider
{
    string? AccountToken { get; }
}

public sealed class InMemoryPlexTokenProvider : IPlexTokenProvider
{
    private volatile string? _token;

    public string? AccountToken => _token;

    public void Set(string? token) => _token = token;
}
