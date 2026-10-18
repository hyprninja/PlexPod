namespace RockPlex.Device.Sync;

/// <summary>Maps device paths (<c>/Music/A/b.flac</c>, as Rockbox and playlists see them) to Windows paths.</summary>
public static class DevicePaths
{
    public const string TempSuffix = ".rockplex-tmp";

    public static string ToLocal(string driveRoot, string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        if (!devicePath.StartsWith('/') || devicePath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not an absolute device path: {devicePath}", nameof(devicePath));
        }

        return Path.Combine(driveRoot, devicePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
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
