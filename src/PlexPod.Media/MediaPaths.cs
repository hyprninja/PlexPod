namespace PlexPod.Media;

/// <summary>The pipeline's folders under <c>%LOCALAPPDATA%\PlexPod\cache\</c>.</summary>
public sealed class MediaPaths
{
    public MediaPaths(string cacheRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(cacheRoot);
        CacheRoot = cacheRoot;
        Directory.CreateDirectory(Work);
        Directory.CreateDirectory(Converted);
        Directory.CreateDirectory(ArtCache);
    }

    public string CacheRoot { get; }

    /// <summary>Per-track scratch folders; always emptied after each track.</summary>
    public string Work => Path.Combine(CacheRoot, "work");

    /// <summary>Converted audio before tagging, so a Plex tag change never re-converts.</summary>
    public string Converted => Path.Combine(CacheRoot, "converted");

    public string ArtCache => Path.Combine(CacheRoot, "art");

    /// <summary>Play history per iPod (M4), next to the cache rather than in it: clearing the cache must not lose it.</summary>
    public string StatsArchive => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(CacheRoot))!, "stats");
}
