namespace PlexPod.Core.Sync;

/// <summary>
/// One file PlexPod owns on the iPod. <see cref="DevicePath"/> is the exact-case absolute device path
/// (<c>/Music/…</c>); <see cref="DesiredPath"/> is the Plex-derived path before any collision suffix,
/// used to notice when Plex metadata moves the track.
/// </summary>
public sealed record ManifestTrack
{
    public required string DevicePath { get; init; }

    public required string DesiredPath { get; init; }

    public required string RatingKey { get; init; }

    public string Guid { get; init; } = string.Empty;

    public long PartId { get; init; }

    /// <summary>Plex part key, which embeds the file's change timestamp; a new key means a new file.</summary>
    public string PartKey { get; init; } = string.Empty;

    /// <summary>Size of the source file on the server.</summary>
    public long SourceSizeBytes { get; init; }

    /// <summary>Size of the file on the iPod.</summary>
    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    /// <summary><see cref="Tagging.TagSet.ContentHash"/> of the tags in the file.</summary>
    public string TagHash { get; init; } = string.Empty;

    /// <summary>Track updatedAt | album updatedAt | tag-policy fingerprint at the time of writing.</summary>
    public string MetadataStamp { get; init; } = string.Empty;

    /// <summary>Values carried over from the original file (D3), kept so tags can be re-derived without a download.</summary>
    public string? SourceComposer { get; init; }

    public string? SourceComment { get; init; }

    public string? MusicBrainzTrackId { get; init; }

    /// <summary>What the pipeline did, for reports ("copied (FLAC 16/44.1)", "converted 24/96 → 16/44.1").</summary>
    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Stock firmware only (M6): the track's iTunesDB record as last written, including the play count
    /// and rating carried from the iPod. The database is rebuilt from these on every sync.
    /// </summary>
    public ItunesDb.ItdbTrack? Stock { get; init; }
}

/// <summary>Another file PlexPod owns, such as an album folder's <c>cover.jpg</c> (PictureFlow).</summary>
public sealed record ManifestExtra(string DevicePath, string Kind, string Sha256, string Owner);

/// <summary>A PlexPod playlist file on the iPod, tracked by Plex rating key (P8).</summary>
public sealed record ManifestPlaylist(string RatingKey, string DevicePath, string Title, string ContentHash);

/// <summary>Everything PlexPod owns on the device, and nothing else. Stored at <c>/.plexpod/manifest.json</c>.</summary>
public sealed record DeviceManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required string DeviceId { get; init; }

    /// <summary><see cref="FirmwareRockbox"/> (the default, for manifests from before M6) or <see cref="FirmwareStock"/>.</summary>
    public string Firmware { get; init; } = FirmwareRockbox;

    public const string FirmwareRockbox = "rockbox";

    public const string FirmwareStock = "stock";

    /// <summary>Stock firmware: what the iTunesDB on the iPod was built from (tracks, stamps, playlists), to tell whether it needs rebuilding.</summary>
    public string? StockDatabaseSignature { get; init; }

    /// <summary>
    /// Stock firmware: SHA-256 of the last Play Counts file whose counts were folded into these tracks.
    /// The same bytes are never folded in twice, even if deleting the file failed or the sync stopped first.
    /// </summary>
    public string? ConsumedPlayCounts { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<ManifestTrack> Tracks { get; init; } = [];

    public IReadOnlyList<ManifestPlaylist> Playlists { get; init; } = [];

    /// <summary>Folders PlexPod created (device paths), removed again when empty.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public IReadOnlyList<ManifestExtra> Extras { get; init; } = [];

    public static DeviceManifest Empty(string deviceId) => new() { DeviceId = deviceId };

    public long ManagedBytes => Tracks.Sum(t => t.SizeBytes);
}
