using PlexPod.Core.Models;
using PlexPod.Core.Selection;
using PlexPod.Core.Sync;
using PlexPod.Core.Tagging;
using PlexPod.Device.Sync;
using PlexPod.Media.Ffmpeg;
using PlexPod.Media.Stats;
using PlexPod.Plex.Library;

namespace PlexPod.Media.Sync;

/// <summary>Everything a sync needs, captured from the app at the moment Sync is pressed.</summary>
public sealed record SyncRequest(
    string DriveRoot,
    string? VolumeLabel,
    string? RockboxTarget,
    IPlexLibraryClient Client,
    FfmpegInstall Ffmpeg,
    LibrarySnapshot Snapshot,
    ResolvedSelection Selection,
    IReadOnlyDictionary<string, PlaylistContents> PlaylistContents,
    IReadOnlyDictionary<string, AudioFormat> FormatOverrides,
    IReadOnlyDictionary<string, long> KnownSizes,
    TagPolicy Policy,
    SelectionProfile Profile,
    StatsOptions? Stats = null);

/// <summary>The read-only look before a sync: what will happen, whether it fits, and anything blocking it.</summary>
public sealed record SyncPreview(
    SyncRequest Request,
    DeviceIdentity? Identity,
    DeviceManifest Manifest,
    ManifestSource ManifestSource,
    AdoptionResult Adoption,
    SyncPlan Plan,
    FitResult Fit,
    IReadOnlyList<string> Blockers)
{
    /// <summary>Things noticed while planning (repaired manifest entries, ignored unsafe paths).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Volume label, size and file system seen while planning; checked again before writing.</summary>
    public string DeviceFingerprint { get; init; } = string.Empty;

    /// <summary>Files known not to be PlexPod's, saved to the iPod so the next plan can skip their tags.</summary>
    public IReadOnlyList<ScannedFile> NotOurs { get; init; } = [];

    /// <summary>Plays and ratings waiting to go to Plex (M4); null when stats return is off.</summary>
    public StatsPreview? Stats { get; init; }

    public bool CanStart => Blockers.Count == 0 && Fit.Outcome != FitOutcome.TooBig;

    public bool UsesMakeRoom => Fit.Outcome == FitOutcome.FitsAfterRemovals;

    /// <summary>Playlists briefly absent in make-room mode (D5).</summary>
    public IReadOnlyList<string> BrieflyAbsentPlaylists =>
        UsesMakeRoom
            ? [.. Plan.PlaylistOps.Where(p => p.NeedsWrite && p.Existing is not null).Select(p => p.Target.Playlist.Title)]
            : [];

    public IReadOnlyList<PlaylistEntry> UnavailableEntries => [.. Plan.PlaylistOps.SelectMany(p => p.SkippedEntries)];
}

public sealed record SyncProgress(string Phase, int Done, int Total, string Current, long BytesWritten);

public sealed record SyncResult(
    bool Completed,
    bool Stopped,
    int Added,
    int Replaced,
    int Retagged,
    int TagsAlreadyCurrent,
    int Moved,
    int Removed,
    int PlaylistsWritten,
    int PlaylistsRemoved,
    long BytesWritten,
    TimeSpan Elapsed,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings,
    string Report)
{
    /// <summary>What step 0 sent to Plex; null when stats return is off or the sync stopped before it.</summary>
    public StatsResult? Stats { get; init; }
}

/// <summary>The iPod disappeared mid-sync. The journaled manifest lets the next sync resume.</summary>
public sealed class DeviceGoneException(string message, Exception? inner = null) : IOException(message, inner);
