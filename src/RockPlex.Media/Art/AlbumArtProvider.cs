using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Tagging;
using RockPlex.Plex.Library;

namespace RockPlex.Media.Art;

/// <summary>
/// One download and one encode per album poster, cached in <c>cache\art\</c> by album key + thumb path
/// (Plex changes the thumb path when a different poster is chosen).
/// </summary>
public sealed class AlbumArtProvider(MediaPaths paths, ILogger<AlbumArtProvider> logger)
{
    public async Task<ArtImages?> GetAsync(IPlexLibraryClient client, AlbumDetail album, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(album);
        if (string.IsNullOrEmpty(album.ThumbPath))
        {
            return null;
        }

        // Only a hash goes into the file name: the rating key comes from the server and is never trusted as a path.
        var stem = Path.Combine(paths.ArtCache, Hash(album.RatingKey + "|" + album.ThumbPath));
        string embeddedPath = stem + "-embed.jpg", coverPath = stem + "-cover.jpg";
        if (File.Exists(embeddedPath) && File.Exists(coverPath))
        {
            return new ArtImages(await File.ReadAllBytesAsync(embeddedPath, ct).ConfigureAwait(false), await File.ReadAllBytesAsync(coverPath, ct).ConfigureAwait(false));
        }

        try
        {
            var source = await client.DownloadImageAsync(album.ThumbPath, ct).ConfigureAwait(false);
            var images = ArtProcessor.Process(source);
            await WriteAtomicAsync(embeddedPath, images.EmbeddedJpeg, ct).ConfigureAwait(false);
            await WriteAtomicAsync(coverPath, images.CoverJpeg, ct).ConfigureAwait(false);
            return images;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or InvalidDataException or FileFormatException
            or System.Runtime.InteropServices.COMException or Plex.Http.PlexProtocolException or ArgumentException or OverflowException)
        {
            // Art is nice to have: a track without art is still synced.
            logger.LogWarning("Album art for {Album} failed: {Message}", album.Title, ex.Message);
            return null;
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
