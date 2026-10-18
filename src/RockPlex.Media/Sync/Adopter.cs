using System.Globalization;
using RockPlex.Core.Sync;
using RockPlex.Core.Tagging;
using RockPlex.Device.Sync;
using RockPlex.Media.Tags;

namespace RockPlex.Media.Sync;

public sealed record AdoptionResult(
    IReadOnlyList<ManifestTrack> Tracks,
    IReadOnlyList<ManifestPlaylist> Playlists,
    IReadOnlyList<ManifestExtra> Extras,
    IReadOnlyList<string> Folders);

/// <summary>
/// Claims files on the iPod that RockPlex demonstrably made but the manifest doesn't list (M3 plan §7,
/// D7): tracks carrying <c>ROCKPLEX_RATINGKEY</c>/<c>ROCKPLEX_PARTID</c> tags, playlists with a
/// <c>#ROCKPLEX ratingKey=</c> header, and <c>cover.jpg</c> files in folders that hold nothing but
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
    public static AdoptionResult Scan(string driveRoot, IReadOnlyCollection<string> unownedFiles, CancellationToken ct, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(unownedFiles);
        var tracks = new List<ManifestTrack>();
        var playlists = new List<ManifestPlaylist>();
        var seen = 0;
        foreach (var devicePath in unownedFiles)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(++seen);
            var ext = Path.GetExtension(devicePath);
            if (devicePath.StartsWith("/Music/", StringComparison.OrdinalIgnoreCase) && AudioExtensions.Contains(ext))
            {
                if (TryAdoptTrack(driveRoot, devicePath) is { } track)
                {
                    tracks.Add(track);
                }
            }
            else if (devicePath.StartsWith(SyncPlanner.PlaylistFolder + "/", StringComparison.OrdinalIgnoreCase)
                && ext.Equals(SyncPlanner.PlaylistExtension, StringComparison.OrdinalIgnoreCase)
                && TryAdoptPlaylist(driveRoot, devicePath) is { } playlist)
            {
                playlists.Add(playlist);
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

        return new AdoptionResult(tracks, playlists, extras, [.. folders]);
    }

    private static ManifestTrack? TryAdoptTrack(string driveRoot, string devicePath)
    {
        var local = DevicePaths.ToLocal(driveRoot, devicePath);
        TagReadBack back;
        try
        {
            back = TagWriter.ReadBack(local);
        }
        catch (Exception ex) when (ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or IOException)
        {
            return null;
        }

        string? Field(string key) => back.Fields.FirstOrDefault(f => f.Key == key).Value;
        var ratingKey = Field(TagSet.RatingKeyKey);
        if (string.IsNullOrEmpty(ratingKey) || !long.TryParse(Field(TagSet.PartIdKey), NumberStyles.None, CultureInfo.InvariantCulture, out var partId))
        {
            return null;
        }

        return new ManifestTrack
        {
            DevicePath = devicePath,
            DesiredPath = devicePath,
            RatingKey = ratingKey,
            Guid = Field(TagSet.GuidKey) ?? string.Empty,
            PartId = partId,
            SizeBytes = new FileInfo(local).Length,

            // No full-file hash here: reading every adopted file back over USB 2.0 made planning take minutes.
            // The ID tags prove ownership, and every file RockPlex copies from now on is read back in full (D6).
            Sha256 = string.Empty,
            TagHash = TagSet.HashFields(back.Fields),
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
        catch (IOException)
        {
            return null;
        }
    }

    private static string Parent(string devicePath) => devicePath[..Math.Max(0, devicePath.LastIndexOf('/'))];
}
