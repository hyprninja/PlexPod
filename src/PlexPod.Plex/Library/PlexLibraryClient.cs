using System.Globalization;
using Microsoft.Extensions.Logging;
using PlexPod.Core.Models;
using PlexPod.Core.Tagging;
using PlexPod.Plex.Dto;
using PlexPod.Plex.Http;
using PlexPod.Plex.Servers;

namespace PlexPod.Plex.Library;

public sealed record MusicSection(string Key, string Title, string? Uuid);

/// <summary>A track's full metadata: stream details plus the MusicBrainz track ID when Plex has one.</summary>
public sealed record PlexTrackDetail(TrackItem Track, string? MusicBrainzTrackId);

/// <summary>
/// Access to one server's music library: read-only, except <see cref="RecordPlayAsync"/> and
/// <see cref="RateAsync"/> (M4 stats return), the only writes PlexPod ever makes to Plex.
/// </summary>
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
    Task<PlexTrackDetail?> GetTrackDetailAsync(string ratingKey, CancellationToken ct);

    Task<AlbumDetail?> GetAlbumDetailAsync(string albumKey, CancellationToken ct);

    Task<ArtistDetail?> GetArtistDetailAsync(string artistKey, CancellationToken ct);

    /// <summary>
    /// Streams the original file to <paramref name="destination"/> (read-only <c>?download=1</c>).
    /// Throws <see cref="PlexProtocolException"/> if the byte count doesn't match <c>Part.size</c>.
    /// </summary>
    Task<long> DownloadPartAsync(MediaPartRef part, Stream destination, IProgress<long>? progress, CancellationToken ct);

    /// <summary>Full-size bytes of an image path such as an album <c>thumb</c>.</summary>
    Task<byte[]> DownloadImageAsync(string imagePath, CancellationToken ct);

    /// <summary>
    /// Records one play of a track, as a player does: a "playing" then a "stopped at the end" timeline
    /// report, so Plex adds it to the play history and play count (time = now; Plex can't back-date).
    /// </summary>
    Task RecordPlayAsync(string ratingKey, long durationMs, CancellationToken ct);

    /// <summary>Sets a track's rating, 0–10; -1 clears it.</summary>
    Task RateAsync(string ratingKey, double rating, CancellationToken ct);
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

    private const string LibraryIdentifier = "com.plexapp.plugins.library";

    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(100);

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

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

    public async Task<PlexTrackDetail?> GetTrackDetailAsync(string ratingKey, CancellationToken ct)
    {
        var item = await GetMetadataAsync(ratingKey, "?includeGuids=1", ct).ConfigureAwait(false);
        return item is null ? null : new PlexTrackDetail(PlexMapper.ToTrack(item), PlexMapper.MusicBrainzId(item));
    }

    public async Task<AlbumDetail?> GetAlbumDetailAsync(string albumKey, CancellationToken ct)
    {
        var item = await GetMetadataAsync(albumKey, string.Empty, ct).ConfigureAwait(false);
        return item is null ? null : PlexMapper.ToAlbumDetail(item);
    }

    public async Task<ArtistDetail?> GetArtistDetailAsync(string artistKey, CancellationToken ct)
    {
        var item = await GetMetadataAsync(artistKey, string.Empty, ct).ConfigureAwait(false);
        return item is null ? null : PlexMapper.ToArtistDetail(item);
    }

    public async Task<long> DownloadPartAsync(MediaPartRef part, Stream destination, IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(destination);

        using var request = CreateRequest(part.Key, "?download=1");
        logger.LogDebug("Download part {PartId}", part.PartId);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[1 << 17];
            long total = 0;
            int read;
            var expected = response.Content.Headers.ContentLength;
            var limit = Math.Min(expected ?? long.MaxValue, options.MaxPartBytes);
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > limit)
                {
                    throw new PlexProtocolException($"Download of part {part.PartId} exceeded {limit:N0} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                progress?.Report(total);
            }

            // HTTP framing (Content-Length or chunked encoding) already guarantees a complete transfer; this
            // makes a short read explicit even if a handler didn't enforce it.
            if (expected is long length && total != length)
            {
                throw new PlexProtocolException($"Download of part {part.PartId} ended early: {total:N0} of {length:N0} bytes.");
            }

            // Plex's stored size goes stale when a file changes on disk after the last scan (e.g. edited tags).
            if (part.SizeBytes > 0 && total != part.SizeBytes)
            {
                logger.LogInformation("Part {PartId}: received {Total:N0} bytes, Plex's library says {Size:N0} (the file changed since Plex last scanned it)",
                    part.PartId, total, part.SizeBytes);
            }

            return total;
        }
    }

    public async Task<byte[]> DownloadImageAsync(string imagePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        using var request = CreateRequest(imagePath, string.Empty);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > options.MaxImageBytes)
        {
            throw new PlexProtocolException($"Image is larger than {options.MaxImageBytes:N0} bytes.");
        }

        using var buffer = new MemoryStream();
        var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var chunk = new byte[1 << 16];
            int read;
            while ((read = await source.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > options.MaxImageBytes)
                {
                    throw new PlexProtocolException($"Image is larger than {options.MaxImageBytes:N0} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }
        }

        return buffer.ToArray();
    }

    /// <remarks>
    /// <c>/:/scrobble</c> only marks an item as played (Plex's docs: it creates no view history), and a
    /// bare timeline report didn't add history either (tested 19 Oct 2026). Plex records history for
    /// playback sessions, so this reports a play as a player does: a play queue for the track, then
    /// "playing" at the start and near the end and "stopped" at the end, all under one session.
    /// 🔬 Experimental: the track's play count and last-played time are logged before and after, so
    /// one real sync shows what Plex recorded. Without a known length, the track is only marked played.
    /// </remarks>
    public async Task RecordPlayAsync(string ratingKey, long durationMs, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ratingKey);
        var key = Uri.EscapeDataString(ratingKey);
        var before = await TryPlayStateAsync(ratingKey, ct).ConfigureAwait(false);
        if (durationMs <= 0)
        {
            await WriteAsync(HttpMethod.Get, "/:/scrobble", $"?key={key}&identifier={LibraryIdentifier}", ct).ConfigureAwait(false);
        }
        else
        {
            var session = Guid.NewGuid().ToString("N");
            var item = string.Create(CultureInfo.InvariantCulture,
                $"?ratingKey={key}&key={Uri.EscapeDataString("/library/metadata/" + ratingKey)}&identifier={LibraryIdentifier}&duration={durationMs}&hasMDE=1");
            var queue = await TryCreatePlayQueueAsync(ratingKey, session, ct).ConfigureAwait(false);
            if (queue is { } q)
            {
                item += string.Create(CultureInfo.InvariantCulture, $"&containerKey={Uri.EscapeDataString("/playQueues/" + q.QueueId)}&playQueueItemID={q.ItemId}");
            }

            var nearEnd = Math.Max(0, durationMs - 1000);
            foreach (var (state, time) in new[] { ("playing", 0L), ("playing", nearEnd), ("stopped", durationMs) })
            {
                await SendWriteAsync(HttpMethod.Get, "/:/timeline",
                    item + string.Create(CultureInfo.InvariantCulture, $"&state={state}&time={time}&playbackTime={time}"), session, ct).ConfigureAwait(false);
            }
        }

        if (options.PlayStateCheckDelay > TimeSpan.Zero)
        {
            await Task.Delay(options.PlayStateCheckDelay, ct).ConfigureAwait(false);
        }

        var after = await TryPlayStateAsync(ratingKey, ct).ConfigureAwait(false);
        logger.LogInformation("Play of {RatingKey} reported: Plex play count {Before} -> {After}, last played {BeforeAt} -> {AfterAt}",
            ratingKey, before?.ViewCount, after?.ViewCount, before?.LastViewedAt, after?.LastViewedAt);
    }

    /// <summary>A play queue holding just this track, as a player creates before playing; null if Plex won't make one.</summary>
    private async Task<(long QueueId, long ItemId)?> TryCreatePlayQueueAsync(string ratingKey, string session, CancellationToken ct)
    {
        var uri = $"server://{connection.MachineIdentifier}/{LibraryIdentifier}/library/metadata/{ratingKey}";
        try
        {
            var body = await SendWriteAsync(HttpMethod.Post, "/playQueues",
                $"?type=audio&uri={Uri.EscapeDataString(uri)}&shuffle=0&repeat=0&continuous=0&own=1", session, ct).ConfigureAwait(false);
            var container = System.Text.Json.JsonSerializer.Deserialize<ContainerEnvelope>(body, PlexJson.Options)?.MediaContainer;
            var itemId = container?.PlayQueueSelectedItemId ?? container?.Metadata?.FirstOrDefault()?.PlayQueueItemId;
            return container?.PlayQueueId is { } queueId && itemId is { } id ? (queueId, id) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException { StatusCode: not null } or System.Text.Json.JsonException)
        {
            logger.LogWarning("Plex didn't create a play queue for {RatingKey} ({Message}); reporting the play without one", ratingKey, ex.Message);
            return null;
        }
    }

    /// <summary>The track's play count and last-played time as Plex has them (for the log); null if it can't be read.</summary>
    private async Task<(int? ViewCount, long? LastViewedAt)?> TryPlayStateAsync(string ratingKey, CancellationToken ct)
    {
        try
        {
            var item = await GetMetadataAsync(ratingKey, string.Empty, ct).ConfigureAwait(false);
            return item is null ? null : (item.ViewCount ?? 0, item.LastViewedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or PlexProtocolException or System.Text.Json.JsonException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public Task RateAsync(string ratingKey, double rating, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ratingKey);
        if (rating != -1 && rating is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Ratings are 0–10, or -1 to clear.");
        }

        var value = rating.ToString("0.#", CultureInfo.InvariantCulture);
        return WriteAsync(HttpMethod.Put, "/:/rate", $"?key={Uri.EscapeDataString(ratingKey)}&identifier={LibraryIdentifier}&rating={value}", ct);
    }

    /// <summary>A write is sent once: never retried, so a dropped connection can't count a play twice.</summary>
    private Task WriteAsync(HttpMethod method, string path, string query, CancellationToken ct) =>
        SendWriteAsync(method, path, query, session: null, ct);

    /// <summary>A write as a player sends it (with a session id when given); returns the response body.</summary>
    private async Task<string> SendWriteAsync(HttpMethod method, string path, string query, string? session, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WriteTimeout);
        using var request = CreateRequest(path, query);
        request.Method = method;
        request.Options.Set(TransientRetryHandler.NoRetry, true);
        if (session is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Plex-Session-Identifier", session);
            request.Headers.TryAddWithoutValidation("X-Plex-Provides", "player");
        }

        logger.LogInformation("{Method} {Uri}", method, PlexRedactor.Redact(request.RequestUri));
        using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
    }

    private async Task<MetadataDto?> GetMetadataAsync(string ratingKey, string query, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(ratingKey);
        var container = await GetContainerAsync($"/library/metadata/{Uri.EscapeDataString(ratingKey)}{query}", null, ct).ConfigureAwait(false);
        return container.Metadata?.FirstOrDefault();
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
        List<MetadataDto>? previous = null;
        for (var pages = 1; ; pages++)
        {
            if (pages > Math.Max(1, options.MaxPages))
            {
                throw new PlexProtocolException($"The Plex server kept sending pages ({options.MaxPages:N0} read) without saying how many there are; stopped.");
            }

            var page = await GetContainerAsync(path, (all.Count, pageSize), ct).ConfigureAwait(false);
            var items = page.Metadata ?? [];

            // A server (or proxy) that ignores X-Plex-Container-Start sends the same page again; without
            // totalSize nothing else would ever end the loop.
            if (previous is { Count: > 0 } && items.Count > 0 && items[0].RatingKey is { } first && first == previous[0].RatingKey)
            {
                if (previous.Count > pageSize)
                {
                    return all; // it ignored the page size too, so the first answer was everything
                }

                throw new PlexProtocolException("The Plex server ignored paging (X-Plex-Container-Start), so the list can't be read completely.");
            }

            previous = items;
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
        // The HttpClient has no overall timeout (downloads can take minutes), so metadata calls get their own.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        ct = timeout.Token;

        using var request = CreateRequest(path, string.Empty);
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

    private HttpRequestMessage CreateRequest(string path, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Combine(connection.BaseUri, path + query));
        request.Headers.TryAddWithoutValidation(PlexHeadersHandler.TokenHeader, connection.AccessToken);
        return request;
    }

    /// <summary>Appends an absolute Plex path to the base, keeping any path prefix (reverse proxies).</summary>
    internal static Uri Combine(Uri baseUri, string path) =>
        new(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path.TrimStart('/'));
}
