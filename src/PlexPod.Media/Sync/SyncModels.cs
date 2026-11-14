using PlexPod.Core.Media;
using PlexPod.Core.Models;
using PlexPod.Core.Selection;
using PlexPod.Core.Sync;
using PlexPod.Core.Tagging;
using PlexPod.Device;
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
    StatsOptions? Stats = null,
    IpodFirmware Firmware = IpodFirmware.Rockbox,
    SignedDatabase? SignedDatabase = null);

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
    /// <summary>
    /// Tracks to copy whose FLAC is above 44.1 kHz as Plex reports it, so converting them needs ffmpeg's soxr
    /// resampler (unless an earlier conversion is cached). Formats Plex hasn't reported aren't counted.
    /// </summary>
    public int TracksNeedingResample => Plan.TrackOps.Count(o =>
        o.Kind is TrackOpKind.Add or TrackOpKind.Replace
        && FormatPolicy.NeedsResample(Request.FormatOverrides.GetValueOrDefault(o.Target.Track.RatingKey) ?? o.Target.Track.Format));

    /// <summary>The plan holds hi-res FLAC to convert but this ffmpeg can't resample (docs/state-machines.md §6).</summary>
    public bool MissingSoxr => !Request.Ffmpeg.HasSoxr && TracksNeedingResample > 0;

    /// <summary>Things noticed while planning (repaired manifest entries, ignored unsafe paths).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Volume label, size and file system seen while planning; checked again before writing.</summary>
    public string DeviceFingerprint { get; init; } = string.Empty;

    /// <summary>Files known not to be PlexPod's, saved to the iPod so the next plan can skip their tags.</summary>
    public IReadOnlyList<ScannedFile> NotOurs { get; init; } = [];

    /// <summary>Plays and ratings waiting to go to Plex (M4); null when stats return is off.</summary>
    public StatsPreview? Stats { get; init; }

    public bool IsStock => Request.Firmware == IpodFirmware.Stock;

    /// <summary>Stock firmware: the iTunesDB is missing, out of date, or has plays to fold in, so it will be rebuilt.</summary>
    public bool StockDatabaseNeedsWrite { get; init; }

    /// <summary>Stock firmware: what the rebuilt database is built from (saved in the manifest once it's on the iPod).</summary>
    public string? StockSignature { get; init; }

    /// <summary>Stock firmware: the selected playlists, written into the database rather than as files.</summary>
    public IReadOnlyList<SyncPlaylistTarget> StockPlaylists { get; init; } = [];

    /// <summary>Stock firmware: plays and rating changes waiting in Play Counts or from an earlier sync.</summary>
    public (int Plays, int Ratings) StockStats { get; init; }

    /// <summary>Stock firmware: selected tracks in formats it can't play (OGG, Opus, …), left out.</summary>
    public IReadOnlyList<string> UnplayableOnStock { get; init; } = [];

    /// <summary>Anything to do at all: file changes, a database rebuild, or plays to send.</summary>
    public bool HasWork => !Plan.IsEmpty || StockDatabaseNeedsWrite
        || Stats is { Plan.IsEmpty: false } || StockStats.Plays + StockStats.Ratings > 0;

    public bool CanStart => Blockers.Count == 0 && Fit.Outcome != FitOutcome.TooBig;

    public bool UsesMakeRoom => Fit.Outcome == FitOutcome.FitsAfterRemovals;

    /// <summary>Playlists briefly absent in make-room mode (D5).</summary>
    public IReadOnlyList<string> BrieflyAbsentPlaylists =>
        !UsesMakeRoom ? []
        : IsStock ? [.. StockPlaylists.Select(p => p.Playlist.Title)]
        : [.. Plan.PlaylistOps.Where(p => p.NeedsWrite && p.Existing is not null).Select(p => p.Target.Playlist.Title)];

    public IReadOnlyList<PlaylistEntry> UnavailableEntries => [.. Plan.PlaylistOps.SelectMany(p => p.SkippedEntries)];
}

public sealed record SyncProgress(SyncPhase Phase, int Done, int Total, string Current, long BytesWritten)
{
    /// <summary>The phase as the user sees it.</summary>
    public string PhaseText => Phase.Describe();
}

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

    /// <summary>How the sync ended, from <see cref="Completed"/> then <see cref="Stopped"/> (docs/state-machines.md §2).</summary>
    public SyncOutcome Outcome => Completed ? SyncOutcome.Complete : Stopped ? SyncOutcome.Stopped : SyncOutcome.FinishedWithProblems;
}

/// <summary>The iPod disappeared mid-sync. The journaled manifest lets the next sync resume.</summary>
public sealed class DeviceGoneException(string message, Exception? inner = null) : IOException(message, inner);
