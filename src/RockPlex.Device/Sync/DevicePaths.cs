namespace RockPlex.Device.Sync;

/// <summary>
/// Maps device paths (<c>/Music/A/b.flac</c>, as Rockbox and playlists see them) to Windows paths.
/// Device paths can come from files on the iPod (manifest, playlists), so they are treated as
/// untrusted: anything that could resolve outside the iPod's drive is rejected.
/// </summary>
public static class DevicePaths
{
    public const string TempSuffix = ".rockplex-tmp";

    /// <summary>The only top-level folders RockPlex ever writes to or deletes from (besides <c>/.rockplex</c>).</summary>
    public static readonly IReadOnlyList<string> ManagedRoots = ["/Music/", "/Playlists/"];

    /// <exception cref="ArgumentException">Not a safe absolute device path, or it would resolve outside the drive.</exception>
    public static string ToLocal(string driveRoot, string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        if (!IsWellFormed(devicePath))
        {
            throw new ArgumentException($"Not a safe absolute device path: {devicePath}", nameof(devicePath));
        }

        var root = Path.GetFullPath(driveRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, devicePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Device path resolves outside the device: {devicePath}", nameof(devicePath));
        }

        return full;
    }

    /// <summary>
    /// Absolute, forward slashes only, no drive letters, backslashes or control characters, and no
    /// empty, "." or ".." segments. Dots inside a name are fine ("08 ...It's Just Wide-Open Field.m4a");
    /// a segment ending in a dot or space is rejected because Windows would silently strip it.
    /// </summary>
    public static bool IsWellFormed(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath) || devicePath[0] != '/' || devicePath.Length < 2)
        {
            return false;
        }

        if (devicePath.Any(c => c is '\\' or ':' || char.IsControl(c)))
        {
            return false;
        }

        return devicePath[1..].Split('/').All(segment =>
            segment.Length > 0 && segment is not ("." or "..") && segment[^1] is not ('.' or ' '));
    }

    /// <summary>True for a well-formed path inside <c>/Music/</c> or <c>/Playlists/</c>, the folders RockPlex manages.</summary>
    public static bool IsManaged(string? devicePath) =>
        IsWellFormed(devicePath) && ManagedRoots.Any(root => devicePath!.StartsWith(root, StringComparison.OrdinalIgnoreCase));

    /// <exception cref="InvalidOperationException">The path is outside the folders RockPlex manages.</exception>
    public static void EnsureManaged(string devicePath)
    {
        if (!IsManaged(devicePath))
        {
            throw new InvalidOperationException($"Refusing to change {devicePath}: RockPlex only manages /Music and /Playlists.");
        }
    }

    public static string ToDevice(string driveRoot, string localPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        var relative = Path.GetRelativePath(driveRoot, localPath);
        return "/" + relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Every folder above <paramref name="devicePath"/>, shallowest first, excluding the root.</summary>
    public static IEnumerable<string> ParentFolders(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        var parts = devicePath.Trim('/').Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            yield return "/" + string.Join('/', parts[..i]);
        }
    }
}
