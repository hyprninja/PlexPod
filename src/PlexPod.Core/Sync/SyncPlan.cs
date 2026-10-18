using PlexPod.Core.Models;

namespace PlexPod.Core.Sync;

/// <summary>A selected track as the planner sees it.</summary>
/// <param name="DesiredPath">Plex-derived device path from <see cref="Tagging.DevicePathBuilder"/>, before collision suffixes.</param>
/// <param name="EstimatedBytes">Conservative size on the iPod (never under).</param>
/// <param name="MetadataStamp">Track updatedAt | album updatedAt | tag-policy fingerprint.</param>
public sealed record SyncTrackTarget(TrackItem Track, string DesiredPath, long EstimatedBytes, string MetadataStamp)
{
    public MediaPartRef Part => Track.Parts[0];

    public static string Stamp(long? trackUpdatedAt, long? albumUpdatedAt, string policyFingerprint) =>
        FormattableString.Invariant($"{trackUpdatedAt ?? 0}|{albumUpdatedAt ?? 0}|{policyFingerprint}");
}

/// <summary>A selected playlist with its loaded entries.</summary>
public sealed record SyncPlaylistTarget(PlaylistItem Playlist, PlaylistContents Contents);

public enum TrackOpKind
{
    /// <summary>Not on the iPod yet: produce and copy.</summary>
    Add,

    /// <summary>New file in Plex (part id/key/size changed): produce and copy over.</summary>
    Replace,

    /// <summary>Same audio, metadata stamp changed: re-derive the tags; rewrite only if they differ.</summary>
    Retag,

    /// <summary>Plex-derived path changed: rename on the device (then re-tag if the stamp changed too).</summary>
    Move,

    Keep,
}

/// <param name="DevicePath">Where the file ends up (exact case).</param>
/// <param name="Existing">The manifest entry being updated; null for adds.</param>
/// <param name="AlsoRetag">For moves: the metadata stamp changed as well.</param>
public sealed record TrackOp(TrackOpKind Kind, SyncTrackTarget Target, string DevicePath, ManifestTrack? Existing, bool AlsoRetag = false)
{
    public bool NeedsPipeline => Kind is TrackOpKind.Add or TrackOpKind.Replace or TrackOpKind.Retag || AlsoRetag;
}

/// <param name="Content">Full <c>.m3u8</c> text as it will be written.</param>
/// <param name="Existing">The manifest entry, if this playlist is already on the iPod.</param>
/// <param name="NeedsWrite">False when the file already holds exactly this content at this path.</param>
public sealed record PlaylistOp(SyncPlaylistTarget Target, string DevicePath, string Content, string ContentHash, ManifestPlaylist? Existing, bool NeedsWrite, IReadOnlyList<PlaylistEntry> SkippedEntries);

/// <summary>Everything one sync will do, worked out before anything is written.</summary>
public sealed record SyncPlan(
    IReadOnlyList<TrackOp> TrackOps,
    IReadOnlyList<ManifestTrack> TrackRemovals,
    IReadOnlyList<PlaylistOp> PlaylistOps,
    IReadOnlyList<ManifestPlaylist> PlaylistRemovals)
{
    public int Count(TrackOpKind kind) => TrackOps.Count(o => o.Kind == kind);

    public bool IsEmpty =>
        TrackOps.All(o => o.Kind == TrackOpKind.Keep)
        && TrackRemovals.Count == 0
        && PlaylistRemovals.Count == 0
        && PlaylistOps.All(p => !p.NeedsWrite);
}
