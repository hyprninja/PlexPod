using PlexPod.Core.Models;
using PlexPod.Core.Tagging;

namespace PlexPod.Core.Sync;

/// <summary>
/// Pure diff of the selection against the manifest (M3 plan §3). Never touches the device: the
/// caller supplies the user's own paths so they are never claimed or overwritten.
/// </summary>
public static class SyncPlanner
{
    public const string PlaylistFolder = "/Playlists";
    public const string PlaylistExtension = ".m3u8";
    public const string UserNameClashSuffix = " (PlexPod)";

    /// <param name="userFiles">Device paths of files PlexPod doesn't own (any case), in <c>/Music</c> and <c>/Playlists</c>.</param>
    /// <param name="artistOf">Track artist for <c>#EXTINF</c> lines.</param>
    public static SyncPlan Plan(
        DeviceManifest manifest,
        IReadOnlyList<SyncTrackTarget> targets,
        IReadOnlyList<SyncPlaylistTarget> playlists,
        IEnumerable<string> userFiles,
        Func<TrackItem, string> artistOf)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(userFiles);

        var allocator = new PathAllocator(
            manifest.Tracks.Select(t => t.DevicePath)
                .Concat(manifest.Playlists.Select(p => p.DevicePath))
                .Concat(userFiles));

        // First manifest entry per rating key wins; any duplicates (e.g. from adoption) are removed.
        var byKey = new Dictionary<string, ManifestTrack>(StringComparer.Ordinal);
        var removals = new List<ManifestTrack>();
        foreach (var track in manifest.Tracks)
        {
            if (!byKey.TryAdd(track.RatingKey, track))
            {
                removals.Add(track);
            }
        }

        var ops = new List<TrackOp>();
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in targets.OrderBy(t => t.DesiredPath, StringComparer.Ordinal))
        {
            if (!target.Track.IsAvailable || !targetKeys.Add(target.Track.RatingKey))
            {
                continue;
            }

            ops.Add(PlanTrack(target, byKey.GetValueOrDefault(target.Track.RatingKey), allocator, removals));
        }

        removals.AddRange(byKey.Values.Where(t => !targetKeys.Contains(t.RatingKey)));

        var pathByKey = ops.ToDictionary(o => o.Target.Track.RatingKey, o => o.DevicePath, StringComparer.Ordinal);
        var (playlistOps, playlistRemovals) = PlanPlaylists(manifest, playlists, pathByKey, allocator, artistOf);
        return new SyncPlan(ops, removals, playlistOps, playlistRemovals);
    }

    private static TrackOp PlanTrack(SyncTrackTarget target, ManifestTrack? existing, PathAllocator allocator, List<ManifestTrack> removals)
    {
        if (existing is null)
        {
            return new TrackOp(TrackOpKind.Add, target, allocator.Allocate(target.DesiredPath), null);
        }

        var part = target.Part;
        var newFile = existing.PartId != part.PartId
            || !string.Equals(existing.PartKey, part.Key, StringComparison.Ordinal)
            || (part.SizeBytes > 0 && existing.SourceSizeBytes > 0 && existing.SourceSizeBytes != part.SizeBytes);
        var pathChanged = !string.Equals(existing.DesiredPath, target.DesiredPath, StringComparison.Ordinal);
        var stampChanged = !string.Equals(existing.MetadataStamp, target.MetadataStamp, StringComparison.Ordinal);

        if (newFile)
        {
            if (!pathChanged)
            {
                return new TrackOp(TrackOpKind.Replace, target, existing.DevicePath, existing);
            }

            // New file at a new path (e.g. MP3 replaced by FLAC): write the new one, remove the old one last.
            removals.Add(existing);
            return new TrackOp(TrackOpKind.Replace, target, allocator.Allocate(target.DesiredPath), existing);
        }

        if (pathChanged)
        {
            return new TrackOp(TrackOpKind.Move, target, allocator.Allocate(target.DesiredPath, existing.DevicePath), existing, stampChanged);
        }

        return stampChanged
            ? new TrackOp(TrackOpKind.Retag, target, existing.DevicePath, existing)
            : new TrackOp(TrackOpKind.Keep, target, existing.DevicePath, existing);
    }

    private static (List<PlaylistOp> Ops, List<ManifestPlaylist> Removals) PlanPlaylists(
        DeviceManifest manifest,
        IReadOnlyList<SyncPlaylistTarget> playlists,
        IReadOnlyDictionary<string, string> pathByKey,
        PathAllocator allocator,
        Func<TrackItem, string> artistOf)
    {
        var existingByKey = manifest.Playlists
            .GroupBy(p => p.RatingKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var removals = manifest.Playlists.GroupBy(p => p.RatingKey, StringComparer.Ordinal).SelectMany(g => g.Skip(1)).ToList();
        var ops = new List<PlaylistOp>();
        var selected = new HashSet<string>(StringComparer.Ordinal);

        foreach (var target in playlists.OrderBy(p => p.Playlist.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!selected.Add(target.Playlist.RatingKey))
            {
                continue;
            }

            var existing = existingByKey.GetValueOrDefault(target.Playlist.RatingKey);
            var desired = $"{PlaylistFolder}/{DevicePathBuilder.Sanitize(target.Playlist.Title, "Playlist")}{PlaylistExtension}";

            // Keep the current file if it is this playlist's own (possibly suffixed) name; a rename in Plex moves it.
            var path = existing is not null && SameBaseName(existing.DevicePath, desired)
                ? existing.DevicePath
                : allocator.Allocate(desired, UserNameClashSuffix, existing?.DevicePath);
            if (existing is not null && !string.Equals(existing.DevicePath, path, StringComparison.Ordinal))
            {
                removals.Add(existing);
            }

            var (content, skipped) = PlaylistWriter.Build(target, pathByKey, artistOf);
            var hash = PlaylistWriter.Hash(content);
            var needsWrite = existing is null || !string.Equals(existing.DevicePath, path, StringComparison.Ordinal) || existing.ContentHash != hash;
            ops.Add(new PlaylistOp(target, path, content, hash, existing, needsWrite, skipped));
        }

        removals.AddRange(existingByKey.Values.Where(p => !selected.Contains(p.RatingKey)));
        return (ops, removals);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="desired"/> or a suffixed variant of it.</summary>
    private static bool SameBaseName(string path, string desired)
    {
        if (string.Equals(path, desired, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = desired[..^PlaylistExtension.Length];
        return path.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase) && path.EndsWith(")" + PlaylistExtension, StringComparison.OrdinalIgnoreCase);
    }
}
