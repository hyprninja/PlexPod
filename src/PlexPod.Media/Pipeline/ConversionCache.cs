using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PlexPod.Core.Media;

namespace PlexPod.Media.Pipeline;

/// <summary>
/// Converted (untagged) audio keyed by the source file (part id + part key, which embeds the file's
/// change stamp) + conversion profile, with least-recently-used eviction above the size cap. Editing
/// metadata in Plex never invalidates a conversion. Copies are never cached.
/// </summary>
public sealed class ConversionCache(MediaPaths paths)
{
    /// <summary>Bump when the ffmpeg commands change, so old outputs aren't reused.</summary>
    public const int ProfileVersion = 2;

    public const long DefaultCapBytes = 20L * 1024 * 1024 * 1024;

    private readonly Lock _gate = new();

    public long CapBytes { get; set; } = DefaultCapBytes;

    public static string Key(long partId, string partKey, FlacAction action) => action switch
    {
        FlacAction.Convert c => string.Create(CultureInfo.InvariantCulture, $"{SourceKey(partId, partKey)}-v{ProfileVersion}-{c.TargetBits}x{c.TargetRateHz}"),
        FlacAction.ReencodeLossless => string.Create(CultureInfo.InvariantCulture, $"{SourceKey(partId, partKey)}-v{ProfileVersion}-lossless"),
        _ => throw new ArgumentException("Copies aren't cached.", nameof(action)),
    };

    public string PathFor(string key) => Path.Combine(paths.Converted, key + ".flac");

    public bool TryGet(string key, out string path)
    {
        path = PathFor(key);
        if (!File.Exists(path))
        {
            return false;
        }

        File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        return true;
    }

    /// <summary>Moves a finished conversion into the cache, then evicts the oldest files above the cap.</summary>
    public string Add(string key, string finishedFile)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            if (File.Exists(path))
            {
                // Produced meanwhile by another export or sync; keep that copy, which may be in use.
                File.Delete(finishedFile);
            }
            else
            {
                File.Move(finishedFile, path);
            }

            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            Evict(keep: path);
        }

        return path;
    }

    public long SizeBytes() => new DirectoryInfo(paths.Converted).EnumerateFiles("*.flac").Sum(f => f.Length);

    /// <summary>Part id + a short hash of the part key (file-name safe; the key changes when the file does).</summary>
    public static string SourceKey(long partId, string partKey) =>
        string.Create(CultureInfo.InvariantCulture, $"{partId}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(partKey ?? string.Empty)))[..12]}");

    /// <summary>
    /// Sizes of cached conversions for the current profile, keyed by <see cref="SourceKey"/>, so the
    /// capacity bar can use real sizes instead of estimates.
    /// </summary>
    public IReadOnlyDictionary<string, long> SizesBySource()
    {
        var marker = string.Create(CultureInfo.InvariantCulture, $"-v{ProfileVersion}-");
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var file in new DirectoryInfo(paths.Converted).EnumerateFiles("*.flac"))
        {
            var name = Path.GetFileNameWithoutExtension(file.Name);
            var cut = name.IndexOf(marker, StringComparison.Ordinal);
            if (cut > 0)
            {
                sizes[name[..cut]] = file.Length;
            }
        }

        return sizes;
    }

    private void Evict(string keep)
    {
        var files = new DirectoryInfo(paths.Converted).EnumerateFiles("*.flac").OrderBy(f => f.LastAccessTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= CapBytes)
            {
                break;
            }

            if (string.Equals(file.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            total -= file.Length;
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // In use by another export; try again next time.
            }
        }
    }
}
