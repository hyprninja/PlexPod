namespace PlexPod.Core.Models;

/// <summary>A Plex album artist (the artist → album hierarchy, not the per-track artist).</summary>
public sealed record ArtistItem(string RatingKey, string Guid, string Title, string? ThumbPath = null);

public sealed record AlbumItem(
    string RatingKey,
    string Guid,
    string Title,
    string ArtistKey,
    string ArtistTitle,
    int? Year,
    int TrackCount,
    string? ThumbPath = null,
    long? UpdatedAt = null);

/// <summary>
/// Audio properties as Plex reports them. Bit depth and sample rate are usually missing from
/// list responses and only arrive with <c>/library/metadata/{ratingKey}</c>.
/// </summary>
public sealed record AudioFormat(
    string? Container,
    string? Codec,
    int? BitrateKbps = null,
    int? BitDepth = null,
    int? SampleRateHz = null,
    int? Channels = null)
{
    public static readonly AudioFormat Unknown = new(null, null);

    /// <summary>True when the codec is FLAC, or the codec is unknown and the container is FLAC.</summary>
    public bool IsFlac => Codec is not null
        ? string.Equals(Codec, "flac", StringComparison.OrdinalIgnoreCase)
        : string.Equals(Container, "flac", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One file of a Plex media item. <see cref="Key"/> is the download path (M2).</summary>
public sealed record MediaPartRef(long PartId, string Key, long SizeBytes, string? Container);

/// <summary>
/// A Plex track. <see cref="Format"/> and <see cref="Parts"/> come from the same (first) Media
/// element. A track with no parts has nothing to copy and counts as unavailable.
/// </summary>
public sealed record TrackItem(
    string RatingKey,
    string Guid,
    string Title,
    string AlbumKey,
    string ArtistKey,
    string? TrackArtist,
    int? Index,
    int? DiscIndex,
    long DurationMs,
    AudioFormat Format,
    IReadOnlyList<MediaPartRef> Parts,
    long? UpdatedAt = null,
    double? UserRating = null)
{
    public bool IsAvailable => Parts.Count > 0;

    public long SourceSizeBytes => Parts.Sum(p => p.SizeBytes);
}

public sealed record PlaylistItem(
    string RatingKey,
    string Guid,
    string Title,
    bool IsSmart,
    int LeafCount,
    long DurationMs,
    string ItemsKey);

/// <summary>
/// One playlist line. Plex gives every line its own <see cref="PlaylistItemId"/>, so the same
/// track may appear more than once; order and duplicates are preserved.
/// </summary>
public sealed record PlaylistEntry(long PlaylistItemId, TrackItem? Track, bool Unavailable);

public sealed record PlaylistContents(string PlaylistRatingKey, IReadOnlyList<PlaylistEntry> Entries);
