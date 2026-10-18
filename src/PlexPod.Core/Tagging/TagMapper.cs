using System.Globalization;
using PlexPod.Core.Models;

namespace PlexPod.Core.Tagging;

/// <summary>Album fields beyond the list response: genres, styles, label and release date.</summary>
public sealed record AlbumDetail(
    string RatingKey,
    string Title,
    string ArtistKey,
    string ArtistTitle,
    int? Year,
    string? OriginallyAvailableAt,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Styles,
    string? Studio,
    int LeafCount,
    string? ThumbPath);

public sealed record ArtistDetail(string RatingKey, string Title, IReadOnlyList<string> Genres);

/// <summary>The only values taken from the original file's tags (D3); everything else is dropped.</summary>
public sealed record SourceTags(string? Composer, string? Comment)
{
    public static readonly SourceTags None = new(null, null);
}

/// <summary>Everything <see cref="TagMapper"/> needs for one track.</summary>
public sealed record TagSource(
    TrackItem Track,
    AlbumDetail Album,
    ArtistDetail? Artist,
    IReadOnlyList<TrackItem> AlbumTracks,
    string? MusicBrainzTrackId,
    SourceTags Original);

/// <summary>Plex metadata → the canonical tag set (pipeline §4.2), filtered by <see cref="TagPolicy"/>.</summary>
public static class TagMapper
{
    public const string VariousArtists = "Various Artists";

    public static TagSet Map(TagSource source, TagPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(policy);

        var track = source.Track;
        var album = source.Album;
        var albumArtist = Clean(album.ArtistTitle);
        var part = track.Parts.Count > 0 ? track.Parts[0].PartId : 0;

        return new TagSet
        {
            Title = policy.Title ? Clean(track.Title) : null,
            Artist = policy.Artist ? Clean(track.TrackArtist) ?? albumArtist : null,
            AlbumArtist = policy.AlbumArtist ? albumArtist : null,
            Album = policy.Album ? Clean(album.Title) : null,
            TrackNumber = policy.TrackNumber ? Positive(track.Index) : null,
            TrackTotal = policy.TrackTotal ? TrackTotal(source) : null,
            DiscNumber = policy.DiscNumber ? Positive(track.DiscIndex) : null,
            DiscTotal = policy.DiscTotal ? DiscTotal(source) : null,
            Date = policy.Date ? Date(album) : null,
            Genre = policy.Genre ? First(album.Genres) ?? First(source.Artist?.Genres) : null,
            Grouping = policy.Grouping ? First(album.Styles) : null,
            Compilation = policy.Compilation && string.Equals(albumArtist, VariousArtists, StringComparison.OrdinalIgnoreCase),
            Label = policy.Label ? Clean(album.Studio) : null,
            MusicBrainzTrackId = policy.MusicBrainzTrackId ? Clean(source.MusicBrainzTrackId) : null,
            Composer = policy.Composer ? Clean(source.Original.Composer) : null,
            Comment = policy.Comment ? Clean(source.Original.Comment) : null,
            PlexPodRatingKey = track.RatingKey,
            PlexPodGuid = track.Guid,
            PlexPodPartId = part,
        };
    }

    /// <summary>YYYY-MM-DD from <c>originallyAvailableAt</c>, else the album year.</summary>
    public static string? Date(AlbumDetail album)
    {
        ArgumentNullException.ThrowIfNull(album);
        var released = Clean(album.OriginallyAvailableAt);
        if (released is not null && DateOnly.TryParse(released.Length > 10 ? released[..10] : released, CultureInfo.InvariantCulture, out var date))
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return album.Year is int y and > 0 ? y.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Tracks on the same disc; falls back to the album's leaf count for single-disc albums.</summary>
    private static int? TrackTotal(TagSource source)
    {
        var disc = source.Track.DiscIndex ?? 1;
        var sameDisc = source.AlbumTracks.Count(t => (t.DiscIndex ?? 1) == disc);
        if (sameDisc > 0)
        {
            return sameDisc;
        }

        return DiscTotal(source) is null or 1 ? Positive(source.Album.LeafCount) : null;
    }

    private static int? DiscTotal(TagSource source)
    {
        var max = source.AlbumTracks.Select(t => t.DiscIndex ?? 0).DefaultIfEmpty(0).Max();
        return Positive(Math.Max(max, source.Track.DiscIndex ?? 0));
    }

    private static string? First(IReadOnlyList<string>? values) => values?.Select(Clean).FirstOrDefault(v => v is not null);

    private static int? Positive(int? value) => value is > 0 ? value : null;

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
