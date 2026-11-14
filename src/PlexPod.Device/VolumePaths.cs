namespace PlexPod.Device;

/// <summary>Which mounted volume a path is on, on Windows (drive letters) and macOS (<c>/Volumes/…</c>).</summary>
public static class VolumePaths
{
    /// <summary>Where macOS mounts removable volumes such as the iPod.</summary>
    public const string MacVolumes = "/Volumes/";

    /// <summary>
    /// The root of the volume holding <paramref name="path"/>: <c>X:\</c> on Windows, exactly as
    /// <see cref="Path.GetPathRoot(string)"/> gives it; <c>/Volumes/NAME</c> on macOS for anything under
    /// <c>/Volumes</c>, otherwise <c>/</c> (the startup disk).
    /// </summary>
    public static string RootOf(string path) => RootOf(path, OperatingSystem.IsWindows());

    internal static string RootOf(string path, bool windows)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (windows)
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        }

        var full = Path.GetFullPath(path);
        if (!full.StartsWith(MacVolumes, StringComparison.Ordinal))
        {
            return "/";
        }

        var end = full.IndexOf('/', MacVolumes.Length);
        return end < 0 ? full : full[..end];
    }
}
