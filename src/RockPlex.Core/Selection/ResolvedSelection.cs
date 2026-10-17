namespace RockPlex.Core.Selection;

public enum RefKind
{
    Playlist,
    Artist,
    Album,
}

/// <summary>A saved pick that no longer exists in Plex (no match by rating key or guid).</summary>
public sealed record MissingRef(RefKind Kind, string RatingKey, string Guid, string Title);

/// <summary>A selected playlist's line that cannot be copied (file deleted or offline in Plex).</summary>
public sealed record UnavailablePlaylistEntry(
    string PlaylistRatingKey,
    string PlaylistTitle,
    long PlaylistItemId,
    string? TrackRatingKey,
    string? TrackTitle);

/// <summary>
/// How many tracks each source contributed. Each track is counted once, under the first source
/// that includes it (playlists → artists → albums → all songs), so the counts add up to the total.
/// </summary>
public sealed record SourceCounts(int FromPlaylists, int FromArtists, int FromAlbums, int FromAllSongs);

public sealed record ResolvedSelection(
    IReadOnlyList<string> TrackKeys,
    long TotalEstimatedBytes,
    bool IsEstimateExact,
    SourceCounts Counts,
    IReadOnlyList<string> PlaylistKeys,
    IReadOnlyList<UnavailablePlaylistEntry> UnavailablePlaylistEntries,
    IReadOnlyList<MissingRef> MissingRefs,
    IReadOnlyList<string> PendingPlaylistKeys,
    IReadOnlyList<string> UnknownFormatTrackKeys,
    int ConversionCount,
    int UnknownFormatCount,
    int UnavailableTrackCount)
{
    public static readonly ResolvedSelection Empty =
        new([], 0, true, new SourceCounts(0, 0, 0, 0), [], [], [], [], [], 0, 0, 0);

    public int TrackCount => TrackKeys.Count;
}
