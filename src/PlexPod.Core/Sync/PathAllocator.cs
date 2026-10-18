namespace PlexPod.Core.Sync;

/// <summary>
/// Hands out device paths without collisions. FAT32 is case-insensitive, so comparisons ignore case;
/// paths are returned with the exact case they will be written in. Paths already used by PlexPod
/// files or by the user's own files are never handed out again.
/// </summary>
public sealed class PathAllocator
{
    private readonly HashSet<string> _occupied;

    public PathAllocator(IEnumerable<string> occupied)
    {
        ArgumentNullException.ThrowIfNull(occupied);
        _occupied = new HashSet<string>(occupied, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsOccupied(string path) => _occupied.Contains(path);

    /// <summary>
    /// Returns <paramref name="desired"/>, or <c>desired (2)</c>, <c>(3)</c>… before the extension.
    /// <paramref name="ownPath"/> (the file's current path) doesn't count as a collision.
    /// </summary>
    public string Allocate(string desired, string? ownPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(desired);
        if (ownPath is not null && string.Equals(desired, ownPath, StringComparison.OrdinalIgnoreCase))
        {
            return ownPath;
        }

        var candidate = desired;
        var slash = desired.LastIndexOf('/');
        var dot = desired.LastIndexOf('.');
        var hasExtension = dot > slash;
        var stem = hasExtension ? desired[..dot] : desired;
        var extension = hasExtension ? desired[dot..] : string.Empty;
        for (var n = 2; _occupied.Contains(candidate); n++)
        {
            candidate = FormattableString.Invariant($"{stem} ({n}){extension}");
        }

        _occupied.Add(candidate);
        return candidate;
    }

    /// <summary>Like <see cref="Allocate"/>, but the first fallback is a fixed suffix such as " (PlexPod)".</summary>
    public string Allocate(string desired, string firstSuffix, string? ownPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(desired);
        ArgumentNullException.ThrowIfNull(firstSuffix);
        if (ownPath is not null && string.Equals(desired, ownPath, StringComparison.OrdinalIgnoreCase))
        {
            return ownPath;
        }

        if (!_occupied.Contains(desired))
        {
            _occupied.Add(desired);
            return desired;
        }

        var dot = desired.LastIndexOf('.');
        var withSuffix = dot > desired.LastIndexOf('/') ? desired[..dot] + firstSuffix + desired[dot..] : desired + firstSuffix;
        return Allocate(withSuffix, ownPath);
    }
}
