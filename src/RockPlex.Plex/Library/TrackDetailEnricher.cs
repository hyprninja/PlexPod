using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Models;

namespace RockPlex.Plex.Library;

/// <summary>
/// Fills in bit depth and sample rate for selected FLAC tracks, which list responses usually omit.
/// At most <see cref="MaxConcurrency"/> requests run at once; results are cached by part id + updatedAt.
/// </summary>
public sealed class TrackDetailEnricher(ILogger<TrackDetailEnricher> logger)
{
    public const int MaxConcurrency = 6;

    /// <summary>
    /// FLAC at or below this bitrate is almost certainly 16/44.1 and is not fetched. Its size estimate
    /// is the source size either way (conversion only ever shrinks), so the fit check stays safe.
    /// M2 must still read real stream details before deciding whether to convert.
    /// </summary>
    public const int LikelyCdQualityMaxKbps = 1100;

    private static readonly AudioFormat AssumedCdQuality = new("flac", "flac", null, 16, 44_100);

    private readonly ConcurrentDictionary<string, AudioFormat> _cache = new(StringComparer.Ordinal);

    public static bool NeedsDetail(TrackItem track) =>
        track.IsAvailable
        && track.Format.IsFlac
        && (track.Format.BitDepth is null || track.Format.SampleRateHz is null);

    /// <summary>
    /// Resolves formats for <paramref name="tracks"/>, calling <paramref name="onResolved"/> (rating key, format)
    /// as each arrives. Failures leave the track unknown, so its estimate stays conservative.
    /// </summary>
    public async Task EnrichAsync(
        IPlexLibraryClient client,
        IEnumerable<TrackItem> tracks,
        Action<string, AudioFormat> onResolved,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(onResolved);

        var toFetch = new List<TrackItem>();
        foreach (var track in tracks.Where(NeedsDetail))
        {
            if (_cache.TryGetValue(CacheKey(track), out var cached))
            {
                onResolved(track.RatingKey, cached);
            }
            else if (track.Format.BitrateKbps is int kbps && kbps > 0 && kbps <= LikelyCdQualityMaxKbps)
            {
                onResolved(track.RatingKey, AssumedCdQuality with { BitrateKbps = kbps });
            }
            else
            {
                toFetch.Add(track);
            }
        }

        await Parallel.ForEachAsync(
            toFetch,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrency, CancellationToken = ct },
            async (track, token) =>
            {
                try
                {
                    var detail = await client.GetTrackDetailAsync(track.RatingKey, token).ConfigureAwait(false);
                    if (detail?.Format is { BitDepth: not null, SampleRateHz: not null } format)
                    {
                        _cache[CacheKey(track)] = format;
                        onResolved(track.RatingKey, format);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or Http.PlexProtocolException or System.Text.Json.JsonException)
                {
                    logger.LogWarning("Track detail for {RatingKey} failed: {Message}", track.RatingKey, ex.Message);
                }
            }).ConfigureAwait(false);
    }

    private static string CacheKey(TrackItem track) =>
        string.Create(CultureInfo.InvariantCulture, $"{track.Parts[0].PartId}:{track.UpdatedAt}");
}
