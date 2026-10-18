namespace PlexPod.Core.ItunesDb;

/// <summary>
/// One song as the stock firmware sees it (an <c>mhit</c> plus its strings). Times are the iPod's
/// local wall-clock time, as its clock has no time zone. Field meanings follow libgpod's
/// <c>Itdb_Track</c>; everything not listed is written as libgpod writes it for a new track.
/// </summary>
public sealed record ItdbTrack
{
    /// <summary>Stable 64-bit id. Kept in the manifest so it survives rebuilds (Play Counts and On-The-Go lists refer to tracks).</summary>
    public required ulong Dbid { get; init; }

    /// <summary>Colon form, e.g. <c>:iPod_Control:Music:F03:ABCD.m4a</c> (see <see cref="IpodPath"/>).</summary>
    public required string IpodPath { get; init; }

    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? Album { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Genre { get; init; }

    public string? Composer { get; init; }

    public string? Comment { get; init; }

    public string? Grouping { get; init; }

    public string? SortArtist { get; init; }

    public string? SortAlbumArtist { get; init; }

    public required ItdbFileKind Kind { get; init; }

    public bool Compilation { get; init; }

    /// <summary>0–100 in steps of 20 (stars × 20), as the firmware stores it.</summary>
    public byte Rating { get; init; }

    public uint SizeBytes { get; init; }

    public uint LengthMs { get; init; }

    public uint TrackNumber { get; init; }

    public uint TrackCount { get; init; }

    public uint DiscNumber { get; init; }

    public uint DiscCount { get; init; }

    public uint Year { get; init; }

    /// <summary>kbit/s.</summary>
    public uint Bitrate { get; init; }

    public uint SampleRate { get; init; }

    public uint PlayCount { get; init; }

    public uint SkipCount { get; init; }

    public DateTime? LastPlayed { get; init; }

    public DateTime? LastSkipped { get; init; }

    public DateTime? Added { get; init; }

    public DateTime? Modified { get; init; }

    /// <summary>Gapless playback (firmware 1.2+): encoder delay and padding in samples, and the real sample count.</summary>
    public uint Pregap { get; init; }

    public uint Postgap { get; init; }

    public ulong SampleCount { get; init; }

    public uint GaplessData { get; init; }

    public bool GaplessTrack { get; init; }

    public bool GaplessAlbum { get; init; }

    /// <summary>The track's cover in the ArtworkDB (its <c>mhii</c> image id), or 0 for none. Set when the database is built.</summary>
    public uint ArtworkId { get; init; }

    /// <summary>Size of the source image, recorded beside the artwork link.</summary>
    public uint ArtworkSize { get; init; }
}

/// <summary>The audio formats the stock firmware plays, with the bytes and strings it expects for each.</summary>
public enum ItdbFileKind
{
    Mp3,
    Aac,
    AppleLossless,
    Wav,
    Aiff,
}

/// <summary>A visible playlist: entries are track <see cref="ItdbTrack.Dbid"/>s, in order, duplicates allowed.</summary>
public sealed record ItdbPlaylist(ulong Id, string Name, IReadOnlyList<ulong> Entries);

/// <summary>Everything one <c>iTunesDB</c> holds. The master playlist (every track) is implied.</summary>
/// <param name="DatabaseId">mhbd id; kept across rebuilds.</param>
/// <param name="LibraryId">The library's persistent id; kept across rebuilds.</param>
/// <param name="MasterName">The master playlist's name (iTunes uses the iPod's name).</param>
/// <param name="MasterId">The master playlist's id; kept across rebuilds.</param>
public sealed record ItdbLibrary(
    ulong DatabaseId,
    ulong LibraryId,
    string MasterName,
    ulong MasterId,
    IReadOnlyList<ItdbTrack> Tracks,
    IReadOnlyList<ItdbPlaylist> Playlists);

/// <summary>The bytes are not a database this code can read safely.</summary>
public sealed class ItunesDbFormatException(string message) : Exception(message);

public static class ItdbFileKinds
{
    /// <summary>The <c>mhod</c> 6 description iTunes writes.</summary>
    public static string Description(ItdbFileKind kind) => kind switch
    {
        ItdbFileKind.Mp3 => "MPEG audio file",
        ItdbFileKind.Aac => "AAC audio file",
        ItdbFileKind.AppleLossless => "Apple Lossless audio file",
        ItdbFileKind.Wav => "WAV audio file",
        ItdbFileKind.Aiff => "AIFF audio file",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>For a file extension such as <c>.m4a</c>; null when the stock firmware can't play it.</summary>
    public static ItdbFileKind? FromExtension(string extension, string? codec = null) => extension.ToLowerInvariant() switch
    {
        ".mp3" => ItdbFileKind.Mp3,
        ".m4a" or ".m4b" or ".mp4" => string.Equals(codec, "alac", StringComparison.OrdinalIgnoreCase) ? ItdbFileKind.AppleLossless : ItdbFileKind.Aac,
        ".wav" => ItdbFileKind.Wav,
        ".aif" or ".aiff" => ItdbFileKind.Aiff,
        _ => null,
    };
}
