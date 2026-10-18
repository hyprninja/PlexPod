using System.Globalization;
using PlexPod.Core.Sync;
using PlexPod.Core.Tagging;
using PlexPod.Device.Sync;
using PlexPod.Media.Tags;

namespace PlexPod.Media.Sync;

public sealed record AdoptionResult(
    IReadOnlyList<ManifestTrack> Tracks,
    IReadOnlyList<ManifestPlaylist> Playlists,
    IReadOnlyList<ManifestExtra> Extras,
    IReadOnlyList<string> Folders)
{
    /// <summary>Files whose tags were read this time and turned out not to be PlexPod's.</summary>
    public IReadOnlyList<ScannedFile> NotOurs { get; init; } = [];

    public int Checked { get; init; }
}

/// <summary>A file known not to be PlexPod's, remembered so later plans don't re-read its tags.</summary>
public sealed record ScannedFile(string DevicePath, long SizeBytes, long ModifiedTicks)
{
    public static ScannedFile? Stat(string driveRoot, string devicePath)
    {
        try
        {
            var info = new FileInfo(DevicePaths.ToLocal(driveRoot, devicePath));
            return info.Exists ? new ScannedFile(devicePath, info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Claims files on the iPod that PlexPod demonstrably made but the manifest doesn't list (M3 plan §7,
/// D7): tracks carrying <c>PLEXPOD_RATINGKEY</c>/<c>PLEXPOD_PARTID</c> tags, playlists with a
/// <c>#PLEXPOD ratingKey=</c> header, and <c>cover.jpg</c> files in folders that hold nothing but
/// adopted tracks. Covers the manual test export and files from an interrupted sync whose manifest
/// entry wasn't saved yet. Anything without that proof stays the user's.
/// </summary>
public static class Adopter
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".mp4", ".aac", ".ogg", ".opus", ".alac",
    };

    /// <param name="unownedFiles">Device paths of files under /Music and /Playlists the manifest doesn't list.</param>
    /// <param name="progress">Reports how many files have been looked at.</param>
    /// <param name="knownNotOurs">Files already checked on an earlier plan and unchanged since; their tags aren't re-read.</param>
    public static AdoptionResult Scan(
        string driveRoot,
        IReadOnlyCollection<string> unownedFiles,
        CancellationToken ct,
        IProgress<int>? progress = null,
        Func<string, bool>? knownNotOurs = null)
    {
        ArgumentNullException.ThrowIfNull(unownedFiles);
        var tracks = new List<ManifestTrack>();
        var playlists = new List<ManifestPlaylist>();
        var notOurs = new List<ScannedFile>();
        var seen = 0;
        var checkedCount = 0;
        foreach (var devicePath in unownedFiles)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(++seen);
            if (!DevicePaths.IsManaged(devicePath) || knownNotOurs?.Invoke(devicePath) == true)
            {
                continue;
            }

            var ext = Path.GetExtension(devicePath);
            var isTrack = devicePath.StartsWith("/Music/", StringComparison.OrdinalIgnoreCase) && AudioExtensions.Contains(ext);
            var isPlaylist = devicePath.StartsWith(SyncPlanner.PlaylistFolder + "/", StringComparison.OrdinalIgnoreCase)
                && ext.Equals(SyncPlanner.PlaylistExtension, StringComparison.OrdinalIgnoreCase);
            if (!isTrack && !isPlaylist)
            {
                continue;
            }

            checkedCount++;
            if (isTrack && TryAdoptTrack(driveRoot, devicePath) is { } track)
            {
                tracks.Add(track);
            }
            else if (isPlaylist && TryAdoptPlaylist(driveRoot, devicePath) is { } playlist)
            {
                playlists.Add(playlist);
            }
            else if (ScannedFile.Stat(driveRoot, devicePath) is { } stat)
            {
                notOurs.Add(stat);
            }
        }

        // cover.jpg and the folders themselves, where every audio file in the folder was just adopted.
        var adoptedPaths = tracks.Select(t => t.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = new List<ManifestExtra>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in tracks.Select(t => Parent(t.DevicePath)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inFolder = unownedFiles.Where(f => string.Equals(Parent(f), folder, StringComparison.OrdinalIgnoreCase)).ToList();
            var others = inFolder.Where(f => !adoptedPaths.Contains(f)).ToList();
            if (others.All(f => Path.GetFileName(f).Equals("cover.jpg", StringComparison.OrdinalIgnoreCase)))
            {
                folders.Add(folder);
                var parent = Parent(folder);
                if (parent.Length > "/Music".Length)
                {
                    folders.Add(parent);
                }

                extras.AddRange(others.Select(cover => new ManifestExtra(cover, "cover", string.Empty, folder)));
            }
        }

        return new AdoptionResult(tracks, playlists, extras, [.. folders]) { NotOurs = notOurs, Checked = checkedCount };
    }

    private static ManifestTrack? TryAdoptTrack(string driveRoot, string devicePath)
    {
        var local = DevicePaths.ToLocal(driveRoot, devicePath);
        TagReadBack back;
        try
        {
            back = TagWriter.ReadBack(local);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Malformed or unreadable files (often the user's own) are simply not PlexPod's.
            return null;
        }

        string? Field(string key) => back.Fields.FirstOrDefault(f => f.Key == key).Value;

        // The current ID tags, or the ones written before the rebrand.
        string? Id(string key, string legacyKey) => Field(key) is { Length: > 0 } value ? value : Field(legacyKey);
        var ratingKey = Id(TagSet.RatingKeyKey, TagSet.LegacyRatingKeyKey);
        if (string.IsNullOrEmpty(ratingKey) || !long.TryParse(Id(TagSet.PartIdKey, TagSet.LegacyPartIdKey), NumberStyles.None, CultureInfo.InvariantCulture, out var partId))
        {
            return null;
        }

        return new ManifestTrack
        {
            DevicePath = devicePath,
            DesiredPath = devicePath,
            RatingKey = ratingKey,
            Guid = Id(TagSet.GuidKey, TagSet.LegacyGuidKey) ?? string.Empty,
            PartId = partId,
            SizeBytes = new FileInfo(local).Length,

            // No full-file hash here: reading every adopted file back over USB 2.0 made planning take minutes.
            // The ID tags prove ownership, and every file PlexPod copies from now on is read back in full (D6).
            Sha256 = string.Empty,
            TagHash = TagSet.HashFields(TagSet.WithCurrentIdKeys(back.Fields)),
            SourceComposer = Field(TagSet.ComposerKey),
            SourceComment = Field(TagSet.CommentKey),
            MusicBrainzTrackId = Field(TagSet.MusicBrainzTrackIdKey),
            Action = "adopted",
        };
    }

    private static ManifestPlaylist? TryAdoptPlaylist(string driveRoot, string devicePath)
    {
        try
        {
            var lines = File.ReadLines(DevicePaths.ToLocal(driveRoot, devicePath)).Take(5).ToList();
            var ratingKey = PlaylistWriter.ReadRatingKey(lines);
            return ratingKey is null ? null : new ManifestPlaylist(ratingKey, devicePath, Path.GetFileNameWithoutExtension(devicePath), "adopted");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Parent(string devicePath) => devicePath[..Math.Max(0, devicePath.LastIndexOf('/'))];
}
