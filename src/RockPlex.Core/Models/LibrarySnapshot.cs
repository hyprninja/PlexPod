namespace RockPlex.Core.Models;

/// <summary>An immutable, indexed copy of one Plex music section.</summary>
public sealed class LibrarySnapshot
{
    private readonly Dictionary<string, ArtistItem> _artists;
    private readonly Dictionary<string, AlbumItem> _albums;
    private readonly Dictionary<string, TrackItem> _tracks;
    private readonly Dictionary<string, PlaylistItem> _playlists;
    private readonly ILookup<string, TrackItem> _tracksByArtist;
    private readonly ILookup<string, TrackItem> _tracksByAlbum;

    public LibrarySnapshot(
        string sectionKey,
        IReadOnlyList<ArtistItem> artists,
        IReadOnlyList<AlbumItem> albums,
        IReadOnlyList<TrackItem> tracks,
        IReadOnlyList<PlaylistItem> playlists)
    {
        SectionKey = sectionKey;
        Artists = artists;
        Albums = albums;
        Tracks = tracks;
        Playlists = playlists;

        _artists = ToDictionary(artists, a => a.RatingKey);
        _albums = ToDictionary(albums, a => a.RatingKey);
        _tracks = ToDictionary(tracks, t => t.RatingKey);
        _playlists = ToDictionary(playlists, p => p.RatingKey);
        _tracksByArtist = tracks.ToLookup(t => t.ArtistKey, StringComparer.Ordinal);
        _tracksByAlbum = tracks.ToLookup(t => t.AlbumKey, StringComparer.Ordinal);
    }

    public static LibrarySnapshot Empty(string sectionKey) => new(sectionKey, [], [], [], []);

    public string SectionKey { get; }
    public IReadOnlyList<ArtistItem> Artists { get; }
    public IReadOnlyList<AlbumItem> Albums { get; }
    public IReadOnlyList<TrackItem> Tracks { get; }
    public IReadOnlyList<PlaylistItem> Playlists { get; }

    public ArtistItem? FindArtist(string ratingKey) => _artists.GetValueOrDefault(ratingKey);
    public AlbumItem? FindAlbum(string ratingKey) => _albums.GetValueOrDefault(ratingKey);
    public TrackItem? FindTrack(string ratingKey) => _tracks.GetValueOrDefault(ratingKey);
    public PlaylistItem? FindPlaylist(string ratingKey) => _playlists.GetValueOrDefault(ratingKey);

    public IEnumerable<TrackItem> TracksByArtist(string artistKey) => _tracksByArtist[artistKey];
    public IEnumerable<TrackItem> TracksByAlbum(string albumKey) => _tracksByAlbum[albumKey];

    // Plex should never send duplicate keys; if it does, the first one wins rather than throwing.
    private static Dictionary<string, T> ToDictionary<T>(IReadOnlyList<T> items, Func<T, string> key)
    {
        var result = new Dictionary<string, T>(items.Count, StringComparer.Ordinal);
        foreach (var item in items)
        {
            result.TryAdd(key(item), item);
        }

        return result;
    }
}
