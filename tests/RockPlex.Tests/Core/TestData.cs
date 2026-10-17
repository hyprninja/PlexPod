using RockPlex.Core.Models;

namespace RockPlex.Tests.Core;

internal static class TestData
{
    public static AudioFormat Flac(int? bits, int? rate) => new("flac", "flac", 900, bits, rate, 2);

    public static AudioFormat Mp3 => new("mp3", "mp3", 320);

    public static TrackItem Track(
        string key,
        string album = "al1",
        string artist = "ar1",
        long size = 1_000_000,
        AudioFormat? format = null,
        bool available = true,
        string? guid = null) =>
        new(
            key,
            guid ?? $"plex://track/{key}",
            $"Track {key}",
            album,
            artist,
            null,
            1,
            1,
            200_000,
            format ?? Mp3,
            available ? [new MediaPartRef(long.Parse(key.TrimStart('t'), System.Globalization.CultureInfo.InvariantCulture), $"/library/parts/{key}/file.mp3", size, "mp3")] : []);

    public static ArtistItem Artist(string key, string? guid = null) => new(key, guid ?? $"plex://artist/{key}", $"Artist {key}");

    public static AlbumItem Album(string key, string artist, string? guid = null) =>
        new(key, guid ?? $"plex://album/{key}", $"Album {key}", artist, $"Artist {artist}", 2001, 0);

    public static PlaylistItem Playlist(string key, string? guid = null) =>
        new(key, guid ?? $"plex://playlist/{key}", $"Playlist {key}", false, 0, 0, $"/playlists/{key}/items");

    public static ItemRef Ref(string key, string? guid = null) => new(key, guid ?? string.Empty, key);

    public static PlaylistEntry Entry(long id, TrackItem? track, bool unavailable = false) => new(id, track, unavailable);
}
