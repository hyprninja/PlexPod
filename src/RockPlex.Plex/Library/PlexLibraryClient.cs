using System.Globalization;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Models;
using RockPlex.Plex.Dto;
using RockPlex.Plex.Http;
using RockPlex.Plex.Servers;

namespace RockPlex.Plex.Library;

public sealed record MusicSection(string Key, string Title, string? Uuid);

/// <summary>Read-only access to one server's music library.</summary>
public interface IPlexLibraryClient
{
    PlexServerConnection Connection { get; }

    Task<IReadOnlyList<MusicSection>> GetMusicSectionsAsync(CancellationToken ct);

    Task<IReadOnlyList<ArtistItem>> GetArtistsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct);

    Task<IReadOnlyList<AlbumItem>> GetAlbumsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct);

    Task<IReadOnlyList<TrackItem>> GetTracksAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct);

    Task<IReadOnlyList<PlaylistItem>> GetPlaylistsAsync(CancellationToken ct);

    Task<PlaylistContents> GetPlaylistContentsAsync(PlaylistItem playlist, CancellationToken ct);

    /// <summary>Full metadata for one track, including the audio stream's bit depth and sample rate.</summary>
    Task<TrackItem?> GetTrackDetailAsync(string ratingKey, CancellationToken ct);
}

public interface IPlexLibraryClientFactory
{
    IPlexLibraryClient Create(PlexServerConnection connection);
}

internal sealed class PlexLibraryClientFactory(
    IHttpClientFactory httpFactory,
    PlexClientOptions options,
    ILoggerFactory loggers) : IPlexLibraryClientFactory
{
    public IPlexLibraryClient Create(PlexServerConnection connection) =>
        new PlexLibraryClient(
            httpFactory.CreateClient(PlexLibraryClient.HttpClientName),
            connection,
            options,
            loggers.CreateLogger<PlexLibraryClient>());
}

public sealed class PlexLibraryClient(
    HttpClient http,
    PlexServerConnection connection,
    PlexClientOptions options,
    ILogger<PlexLibraryClient> logger) : IPlexLibraryClient
{
    public const string HttpClientName = "plex.server";

    // ⚠️ Plex metadata type codes.
    private const int ArtistType = 8;
    private const int AlbumType = 9;
    private const int TrackType = 10;

    public PlexServerConnection Connection => connection;

    public async Task<IReadOnlyList<MusicSection>> GetMusicSectionsAsync(CancellationToken ct)
    {
        var container = await GetContainerAsync("/library/sections", null, ct).ConfigureAwait(false);
        return (container.Directory ?? [])
            .Where(d => string.Equals(d.Type, "artist", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(d.Key))
            .Select(d => new MusicSection(d.Key!, d.Title ?? d.Key!, d.Uuid))
            .ToList();
    }

    public Task<IReadOnlyList<ArtistItem>> GetArtistsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) =>
        GetSectionItemsAsync(sectionKey, ArtistType, PlexMapper.ToArtist, progress, ct);

    public Task<IReadOnlyList<AlbumItem>> GetAlbumsAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) =>
        GetSectionItemsAsync(sectionKey, AlbumType, PlexMapper.ToAlbum, progress, ct);

    public Task<IReadOnlyList<TrackItem>> GetTracksAsync(string sectionKey, IProgress<int>? progress, CancellationToken ct) =>
        GetSectionItemsAsync(sectionKey, TrackType, PlexMapper.ToTrack, progress, ct);

    public async Task<IReadOnlyList<PlaylistItem>> GetPlaylistsAsync(CancellationToken ct)
    {
        var items = await GetPagedAsync("/playlists?playlistType=audio", null, ct).ConfigureAwait(false);

        // Filter client-side too, in case the server ignores the parameter.
        return items
            .Where(m => string.Equals(m.PlaylistType, "audio", StringComparison.OrdinalIgnoreCase))
            .Select(PlexMapper.ToPlaylist)
            .ToList();
    }

    public async Task<PlaylistContents> GetPlaylistContentsAsync(PlaylistItem playlist, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        var items = await GetPagedAsync(playlist.ItemsKey, null, ct).ConfigureAwait(false);
        var entries = items.Select(PlexMapper.ToPlaylistEntry).OfType<PlaylistEntry>().ToList();
        return new PlaylistContents(playlist.RatingKey, entries);
    }

    public async Task<TrackItem?> GetTrackDetailAsync(string ratingKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ratingKey);
        var container = await GetContainerAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}", null, ct).ConfigureAwait(false);
        var item = container.Metadata?.FirstOrDefault();
        return item is null ? null : PlexMapper.ToTrack(item);
    }

    private async Task<IReadOnlyList<T>> GetSectionItemsAsync<T>(
        string sectionKey,
        int type,
        Func<MetadataDto, T> map,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sectionKey);
        var path = string.Create(CultureInfo.InvariantCulture, $"/library/sections/{Uri.EscapeDataString(sectionKey)}/all?type={type}");
        var items = await GetPagedAsync(path, progress, ct).ConfigureAwait(false);
        return items.Where(m => !string.IsNullOrEmpty(m.RatingKey)).Select(map).ToList();
    }

    /// <summary>Pages with X-Plex-Container-Start/Size until totalSize is reached or a page comes back short.</summary>
    private async Task<List<MetadataDto>> GetPagedAsync(string path, IProgress<int>? progress, CancellationToken ct)
    {
        var all = new List<MetadataDto>();
        var pageSize = Math.Max(1, options.PageSize);
        while (true)
        {
            var page = await GetContainerAsync(path, (all.Count, pageSize), ct).ConfigureAwait(false);
            var items = page.Metadata ?? [];
            all.AddRange(items);
            progress?.Report(all.Count);

            var total = page.TotalSize;
            if (items.Count == 0 || (total is int t ? all.Count >= t : items.Count < pageSize))
            {
                return all;
            }
        }
    }

    private async Task<MediaContainerDto> GetContainerAsync(string path, (int Start, int Size)? page, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Combine(connection.BaseUri, path));
        request.Headers.TryAddWithoutValidation(PlexHeadersHandler.TokenHeader, connection.AccessToken);
        if (page is var (start, size))
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Container-Start", start.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Plex-Container-Size", size.ToString(CultureInfo.InvariantCulture));
        }

        logger.LogDebug("GET {Uri}", PlexRedactor.Redact(request.RequestUri));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var envelope = await PlexJson.ReadAsync<ContainerEnvelope>(response, ct).ConfigureAwait(false);
        return envelope.MediaContainer ?? new MediaContainerDto();
    }

    /// <summary>Appends an absolute Plex path to the base, keeping any path prefix (reverse proxies).</summary>
    internal static Uri Combine(Uri baseUri, string path) =>
        new(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path.TrimStart('/'));
}
