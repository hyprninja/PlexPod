namespace RockPlex.Media;

/// <summary>The pipeline's folders under <c>%LOCALAPPDATA%\RockPlex\cache\</c>.</summary>
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
}
