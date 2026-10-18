using RockPlex.Core.Media;
using RockPlex.Core.Models;

namespace RockPlex.Core.Selection;

/// <summary>
/// Turns a <see cref="SelectionProfile"/> into the exact set of tracks for the iPod:
/// AllSongs ? every track : playlists ∪ artists ∪ albums, deduplicated by rating key.
/// Every available entry of a selected playlist is included; unavailable entries are reported.
/// </summary>
public static class SelectionResolver
{
    private const int FromPlaylists = 0;
    private const int FromArtists = 1;
    private const int FromAlbums = 2;
    private const int FromAllSongs = 3;

    /// <param name="playlistContents">Loaded playlist items keyed by playlist rating key. A selected
    /// playlist missing from here is listed in <see cref="ResolvedSelection.PendingPlaylistKeys"/>
    /// and makes the estimate inexact.</param>
    /// <param name="formatOverrides">Enriched formats keyed by track rating key.</param>
    /// <param name="knownSizes">Real on-device sizes keyed by track rating key (e.g. an already-converted
    /// file in the cache). These win over estimates and count as exact.</param>
    public static ResolvedSelection Resolve(
        LibrarySnapshot snapshot,
        SelectionProfile profile,
        IReadOnlyDictionary<string, PlaylistContents> playlistContents,
        IReadOnlyDictionary<string, AudioFormat>? formatOverrides = null,
        IReadOnlyDictionary<string, long>? knownSizes = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(playlistContents);

        var missing = new List<MissingRef>();
        var unavailableEntries = new List<UnavailablePlaylistEntry>();
        var pending = new List<string>();
        var playlistKeys = new List<string>();

        // Insertion-ordered set of chosen tracks; counts[source] is bumped by whichever source adds a track first.
        var chosen = new Dictionary<string, TrackItem>(StringComparer.Ordinal);
        var order = new List<string>();
        var counts = new int[4];
        var unavailableTracks = new HashSet<string>(StringComparer.Ordinal);

        void Add(TrackItem track, int source)
        {
            if (!track.IsAvailable)
            {
                unavailableTracks.Add(track.RatingKey);
                return;
            }

            if (chosen.TryAdd(track.RatingKey, track))
            {
                order.Add(track.RatingKey);
                counts[source]++;
            }
        }

        var playlists = ResolveRefs(profile.Playlists, RefKind.Playlist, snapshot.FindPlaylist, snapshot.Playlists, p => p.Guid, missing);
        var artists = ResolveRefs(profile.Artists, RefKind.Artist, snapshot.FindArtist, snapshot.Artists, a => a.Guid, missing);
        var albums = ResolveRefs(profile.Albums, RefKind.Album, snapshot.FindAlbum, snapshot.Albums, a => a.Guid, missing);

        foreach (var playlist in playlists)
        {
            playlistKeys.Add(playlist.RatingKey);
            if (!playlistContents.TryGetValue(playlist.RatingKey, out var contents))
            {
                pending.Add(playlist.RatingKey);
                continue;
            }

            foreach (var entry in contents.Entries)
            {
                // Prefer the library's copy of the track; tracks from another section only have the entry's data.
                var track = entry.Track is null ? null : snapshot.FindTrack(entry.Track.RatingKey) ?? entry.Track;
                if (entry.Unavailable || track is null || !track.IsAvailable)
                {
                    unavailableEntries.Add(new UnavailablePlaylistEntry(
                        playlist.RatingKey, playlist.Title, entry.PlaylistItemId, track?.RatingKey, track?.Title));
                    continue;
                }

                Add(track, FromPlaylists);
            }
        }

        if (profile.AllSongs)
        {
            // Artist and album picks are redundant under All songs (they were still matched above).
            foreach (var track in snapshot.Tracks)
            {
                Add(track, FromAllSongs);
            }
        }
        else
        {
            foreach (var artist in artists)
            {
                foreach (var track in snapshot.TracksByArtist(artist.RatingKey))
                {
                    Add(track, FromArtists);
                }
            }

            foreach (var album in albums)
            {
                foreach (var track in snapshot.TracksByAlbum(album.RatingKey))
                {
                    Add(track, FromAlbums);
                }
            }
        }

        long total = 0;
        var exact = pending.Count == 0;
        var conversions = 0;
        var unknownKeys = new List<string>();
        foreach (var key in order)
        {
            var track = chosen[key];
            var format = formatOverrides?.GetValueOrDefault(key) ?? track.Format;
            var estimate = SizeEstimator.EstimateBytes(track, format);
            if (knownSizes is not null && knownSizes.TryGetValue(key, out var known))
            {
                // The format decision still comes from the estimate so the conversion count stays right.
                estimate = estimate with { Bytes = known, IsExact = true };
            }

            total += estimate.Bytes;
            exact &= estimate.IsExact;
            if (estimate.Decision is FormatDecision.ConvertFlac)
            {
                conversions++;
            }
            else if (estimate.Decision is FormatDecision.UnknownFlac)
            {
                unknownKeys.Add(key);
            }
        }

        return new ResolvedSelection(
            order,
            total,
            exact,
            new SourceCounts(counts[FromPlaylists], counts[FromArtists], counts[FromAlbums], counts[FromAllSongs]),
            playlistKeys,
            unavailableEntries,
            missing,
            pending,
            unknownKeys,
            conversions,
            unknownKeys.Count,
            unavailableTracks.Count);
    }

    /// <summary>
    /// Matches saved refs by rating key, then by guid; anything else is added to <paramref name="missing"/>.
    /// Two refs that land on the same item yield it once.
    /// </summary>
    public static IReadOnlyList<T> ResolveRefs<T>(
        IReadOnlyList<ItemRef> refs,
        RefKind kind,
        Func<string, T?> findByKey,
        IReadOnlyList<T> all,
        Func<T, string> guidOf,
        ICollection<MissingRef> missing)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(missing);

        var result = new List<T>(refs.Count);
        Dictionary<string, T>? byGuid = null;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var itemRef in refs)
        {
            var match = findByKey(itemRef.RatingKey);
            if (match is null && !string.IsNullOrEmpty(itemRef.Guid))
            {
                byGuid ??= BuildGuidIndex(all, guidOf);
                match = byGuid.GetValueOrDefault(itemRef.Guid);
            }

            if (match is null)
            {
                missing.Add(new MissingRef(kind, itemRef.RatingKey, itemRef.Guid, itemRef.Title));
            }
            else if (seen.Add(match))
            {
                result.Add(match);
            }
        }

        return result;
    }

    private static Dictionary<string, T> BuildGuidIndex<T>(IReadOnlyList<T> all, Func<T, string> guidOf)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in all)
        {
            var guid = guidOf(item);
            if (!string.IsNullOrEmpty(guid))
            {
                index.TryAdd(guid, item);
            }
        }

        return index;
    }
}
