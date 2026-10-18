namespace RockPlex.Core.Sync;

/// <summary>
/// One file RockPlex owns on the iPod. <see cref="DevicePath"/> is the exact-case absolute device path
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
}

/// <summary>Another file RockPlex owns, such as an album folder's <c>cover.jpg</c> (PictureFlow).</summary>
public sealed record ManifestExtra(string DevicePath, string Kind, string Sha256, string Owner);

/// <summary>A RockPlex playlist file on the iPod, tracked by Plex rating key (P8).</summary>
public sealed record ManifestPlaylist(string RatingKey, string DevicePath, string Title, string ContentHash);

/// <summary>Everything RockPlex owns on the device, and nothing else. Stored at <c>/.rockplex/manifest.json</c>.</summary>
public sealed record DeviceManifest
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required string DeviceId { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public IReadOnlyList<ManifestTrack> Tracks { get; init; } = [];

    public IReadOnlyList<ManifestPlaylist> Playlists { get; init; } = [];

    /// <summary>Folders RockPlex created (device paths), removed again when empty.</summary>
    public IReadOnlyList<string> Folders { get; init; } = [];

    public IReadOnlyList<ManifestExtra> Extras { get; init; } = [];

    public static DeviceManifest Empty(string deviceId) => new() { DeviceId = deviceId };

    public long ManagedBytes => Tracks.Sum(t => t.SizeBytes);
}
