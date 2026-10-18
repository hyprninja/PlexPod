namespace PlexPod.Core.ItunesDb;

/// <summary>
/// The firmware's colon paths (<c>:iPod_Control:Music:F03:ABCD.m4a</c>) and the files PlexPod writes
/// for it: <c>iPod_Control/Music/F00</c>…<c>F49</c> with short generated names, as iTunes does.
/// </summary>
public static class IpodPath
{
    public const int MusicFolderCount = 50;

    /// <summary><c>/iPod_Control/Music/F03/ABCD.m4a</c> → <c>:iPod_Control:Music:F03:ABCD.m4a</c>.</summary>
    public static string ToColon(string devicePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicePath);
        if (devicePath[0] != '/' || devicePath.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not an absolute device path: {devicePath}", nameof(devicePath));
        }

        return devicePath.Replace('/', ':');
    }

    /// <summary><c>:iPod_Control:Music:F03:ABCD.m4a</c> → <c>/iPod_Control/Music/F03/ABCD.m4a</c>.</summary>
    public static string FromColon(string ipodPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(ipodPath);
        if (ipodPath[0] != ':' || ipodPath.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Not an iPod colon path: {ipodPath}", nameof(ipodPath));
        }

        return ipodPath.Replace(':', '/');
    }

    /// <summary>
    /// A new file's device path for a track: folder from the key (spread evenly, stable for a given
    /// key), a four-letter name, the extension. <paramref name="attempt"/> picks another name on a
    /// clash.
    /// </summary>
    public static string ForTrack(string key, string extension, int attempt = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentException.ThrowIfNullOrEmpty(extension);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{key}#{attempt}"));
        var folder = (int)(BitConverter.ToUInt32(hash, 0) % MusicFolderCount);
        Span<char> name = stackalloc char[4];
        for (var i = 0; i < name.Length; i++)
        {
            name[i] = (char)('A' + (hash[4 + i] % 26));
        }

        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"/iPod_Control/Music/F{folder:00}/{name}{extension.ToLowerInvariant()}");
    }

    /// <summary>Is this one of the firmware's music files (<c>/iPod_Control/Music/Fnn/…</c>)?</summary>
    public static bool IsMusicFile(string devicePath) =>
        devicePath.StartsWith("/iPod_Control/Music/F", StringComparison.OrdinalIgnoreCase)
        && devicePath.Count(c => c == '/') == 4;
}
