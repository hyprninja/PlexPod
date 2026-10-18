namespace PlexPod.Plex;

/// <summary>Identity sent to Plex in the <c>X-Plex-*</c> headers on every request.</summary>
public sealed class PlexClientOptions
{
    public string Product { get; init; } = "PlexPod";

    public string Version { get; init; } = "1.0.1";

    /// <summary>Stable GUID per install, created on first run and saved in settings.</summary>
    public required string ClientIdentifier { get; init; }

    public string DeviceName { get; init; } = Environment.MachineName;

    public string Platform { get; init; } = "Windows";

    /// <summary>How often the PIN is polled during sign-in.</summary>
    public TimeSpan PinPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for the plex.tv user body (display name) once the token is known valid.</summary>
    public TimeSpan UserInfoTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Timeout for each <c>/identity</c> connection probe.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Largest original file accepted, whatever the server announces (FAT32 can't hold 4 GB files anyway).</summary>
    public long MaxPartBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Largest album poster accepted.</summary>
    public long MaxImageBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Page size for <c>X-Plex-Container-Size</c>.</summary>
    public int PageSize { get; init; } = 1000;

    /// <summary>Pages read for one list before giving up: a server that never says it's done can't keep a load going forever.</summary>
    public int MaxPages { get; init; } = 10_000;
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
