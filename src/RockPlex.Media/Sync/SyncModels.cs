using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using RockPlex.Core.Sync;
using RockPlex.Core.Tagging;
using RockPlex.Device.Sync;
using RockPlex.Media.Ffmpeg;
using RockPlex.Plex.Library;

namespace RockPlex.Media.Sync;

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
    SelectionProfile Profile);

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
    string Report);

/// <summary>The iPod disappeared mid-sync. The journaled manifest lets the next sync resume.</summary>
public sealed class DeviceGoneException(string message, Exception? inner = null) : IOException(message, inner);
