using RockPlex.Core.Sync;

namespace RockPlex.Device.Sync;

public sealed record PlaylistProblem(string PlaylistPath, int LineNumber, string TrackPath, string Reason)
{
    public override string ToString() => $"{PlaylistPath} line {LineNumber}: {TrackPath} — {Reason}";
}

/// <summary>
/// P6: re-reads playlists from the iPod and resolves every track line against the files actually
/// there, with exact case (Rockbox playlist paths are treated as case-sensitive ⚠️).
/// </summary>
public sealed class PlaylistVerifier
{
    private readonly Dictionary<string, HashSet<string>?> _listings = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PlaylistProblem> Verify(string driveRoot, IEnumerable<string> playlistDevicePaths)
    {
        ArgumentNullException.ThrowIfNull(playlistDevicePaths);
        var problems = new List<PlaylistProblem>();
        foreach (var playlist in playlistDevicePaths)
        {
            var local = DevicePaths.ToLocal(driveRoot, playlist);
            if (!File.Exists(local))
            {
                problems.Add(new PlaylistProblem(playlist, 0, playlist, "playlist file missing"));
                continue;
            }

            foreach (var (line, path) in PlaylistWriter.TrackLines(File.ReadAllText(local)))
            {
                if (Resolve(driveRoot, path) is { } reason)
                {
                    problems.Add(new PlaylistProblem(playlist, line, path, reason));
                }
            }
        }

        return problems;
    }

    /// <summary>Null when the path exists with exactly this case; otherwise why not.</summary>
    internal string? Resolve(string driveRoot, string devicePath)
    {
        if (!devicePath.StartsWith('/'))
        {
            return "not an absolute /Music path";
        }

        // Lines come from a file on the iPod: never follow "..", drive letters or backslashes off the device.
        if (!DevicePaths.IsWellFormed(devicePath))
        {
            return "not a valid device path";
        }

        var segments = devicePath.Trim('/').Split('/');
        var current = driveRoot;
        for (var i = 0; i < segments.Length; i++)
        {
            var names = Listing(current);
            if (names is null)
            {
                return "folder missing";
            }

            var segment = segments[i];
            if (!names.Contains(segment))
            {
                return names.Any(n => string.Equals(n, segment, StringComparison.OrdinalIgnoreCase))
                    ? $"case differs at \"{segment}\""
                    : i == segments.Length - 1 ? "file missing" : "folder missing";
            }

            current = Path.Combine(current, segment);
        }

        return File.Exists(current) ? null : "is a folder, not a file";
    }

    private HashSet<string>? Listing(string localFolder)
    {
        if (!_listings.TryGetValue(localFolder, out var names))
        {
            names = Directory.Exists(localFolder)
                ? new HashSet<string>(Directory.EnumerateFileSystemEntries(localFolder).Select(Path.GetFileName).OfType<string>(), StringComparer.Ordinal)
                : null;
            _listings[localFolder] = names;
        }

        return names;
    }
}
