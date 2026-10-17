using RockPlex.Core.Models;
using RockPlex.Plex.Dto;

namespace RockPlex.Plex;

/// <summary>The one place Plex DTOs become Core models.</summary>
internal static class PlexMapper
{
    public const int AudioStreamType = 2;

    public static ArtistItem ToArtist(MetadataDto m) =>
        new(Key(m), m.Guid ?? string.Empty, m.Title ?? string.Empty, m.Thumb);

    public static AlbumItem ToAlbum(MetadataDto m) =>
        new(
            Key(m),
            m.Guid ?? string.Empty,
            m.Title ?? string.Empty,
            m.ParentRatingKey ?? string.Empty,
            m.ParentTitle ?? string.Empty,
            m.Year,
            m.LeafCount ?? 0,
            m.Thumb);

    /// <summary>
    /// Format and parts come from the first Media element (Plex's primary version) ⚠️.
    /// A track without Media or Parts maps to an unavailable track (no parts).
    /// </summary>
    public static TrackItem ToTrack(MetadataDto m)
    {
        var media = m.Media?.FirstOrDefault();
        var parts = media?.Part ?? [];
        var audio = parts
            .SelectMany(p => p.Stream ?? [])
            .FirstOrDefault(s => s.StreamType == AudioStreamType);

        var format = media is null
            ? AudioFormat.Unknown
            : new AudioFormat(
                media.Container ?? parts.FirstOrDefault()?.Container,
                media.AudioCodec ?? audio?.Codec,
                media.Bitrate ?? audio?.Bitrate,
                audio?.BitDepth,
                audio?.SamplingRate,
                media.AudioChannels ?? audio?.Channels);

        var partRefs = parts
            .Where(p => !string.IsNullOrEmpty(p.Key))
            .Select(p => new MediaPartRef(p.Id, p.Key!, p.Size ?? 0, p.Container ?? media?.Container))
            .ToList();

        return new TrackItem(
            Key(m),
            m.Guid ?? string.Empty,
            m.Title ?? string.Empty,
            m.ParentRatingKey ?? string.Empty,
            m.GrandparentRatingKey ?? string.Empty,
            string.IsNullOrEmpty(m.OriginalTitle) ? null : m.OriginalTitle,
            m.Index,
            m.ParentIndex,
            m.Duration ?? 0,
            format,
            partRefs,
            m.UpdatedAt);
    }

    public static PlaylistItem ToPlaylist(MetadataDto m)
    {
        var key = Key(m);
        return new PlaylistItem(
            key,
            m.Guid ?? string.Empty,
            m.Title ?? string.Empty,
            m.Smart,
            m.LeafCount ?? 0,
            m.Duration ?? 0,
            string.IsNullOrEmpty(m.Key) ? $"/playlists/{key}/items" : m.Key);
    }

    /// <summary>Returns null for non-track lines (videos, photos), which playlists on the iPod ignore.</summary>
    public static PlaylistEntry? ToPlaylistEntry(MetadataDto m)
    {
        if (m.Type is not null && !string.Equals(m.Type, "track", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var track = string.IsNullOrEmpty(m.RatingKey) ? null : ToTrack(m);
        return new PlaylistEntry(m.PlaylistItemId ?? 0, track, track is null || !track.IsAvailable);
    }

    private static string Key(MetadataDto m) =>
        m.RatingKey ?? throw new Http.PlexProtocolException($"Plex item '{m.Title}' has no ratingKey.");
}
