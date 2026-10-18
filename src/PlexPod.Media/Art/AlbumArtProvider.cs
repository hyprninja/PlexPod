using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using PlexPod.Core.Tagging;
using PlexPod.Media.Ffmpeg;
using PlexPod.Plex.Library;

namespace PlexPod.Media.Art;

/// <summary>
/// One download and one encode per album poster, cached in <c>cache\art\</c> by album key + thumb path
/// (Plex changes the thumb path when a different poster is chosen). Art is nice to have: any failure
/// is logged and the track syncs without a cover, never fails because of it.
/// </summary>
public sealed class AlbumArtProvider(MediaPaths paths, ILogger<AlbumArtProvider> logger, FfmpegRunner? runner = null)
{
    /// <summary>Turns source bytes into iPod art; replaceable in tests.</summary>
    internal Func<byte[], ArtImages> Processor { get; init; } = ArtProcessor.Process;

    /// <param name="ffmpeg">When given, posters the Windows image decoder rejects are re-decoded through ffmpeg.</param>
    public async Task<ArtImages?> GetAsync(IPlexLibraryClient client, AlbumDetail album, CancellationToken ct, FfmpegInstall? ffmpeg = null)
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
        try
        {
            if (File.Exists(embeddedPath) && File.Exists(coverPath))
            {
                return new ArtImages(await File.ReadAllBytesAsync(embeddedPath, ct).ConfigureAwait(false), await File.ReadAllBytesAsync(coverPath, ct).ConfigureAwait(false));
            }

            var source = await client.DownloadImageAsync(album.ThumbPath, ct).ConfigureAwait(false);
            var images = await ProcessAsync(source, album.Title, ffmpeg, ct).ConfigureAwait(false);
            await WriteAtomicAsync(embeddedPath, images.EmbeddedJpeg, ct).ConfigureAwait(false);
            await WriteAtomicAsync(coverPath, images.CoverJpeg, ct).ConfigureAwait(false);
            return images;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Album art for {Album} failed: {Message}", album.Title, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The Windows decoder rejects some valid posters (unusual metadata or colour profiles). ffmpeg
    /// decodes nearly anything, so it re-encodes those to a plain PNG for a second attempt.
    /// </summary>
    private async Task<ArtImages> ProcessAsync(byte[] source, string album, FfmpegInstall? ffmpeg, CancellationToken ct)
    {
        try
        {
            return Processor(source);
        }
        catch (Exception first) when (first is not OperationCanceledException && ffmpeg is not null && runner is not null)
        {
            logger.LogInformation("Windows couldn't decode the art for {Album} ({Message}); retrying through ffmpeg", album, first.Message);
            var work = Directory.CreateDirectory(Path.Combine(paths.Work, "art-" + Guid.NewGuid().ToString("N"))).FullName;
            try
            {
                var input = Path.Combine(work, "source.img");
                var output = Path.Combine(work, "plain.png");
                await File.WriteAllBytesAsync(input, source, ct).ConfigureAwait(false);
                await runner.RunAsync(ffmpeg.Path,
                    ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-frames:v", "1", "-map_metadata", "-1", "-c:v", "png", "-f", "image2", output],
                    ct).ConfigureAwait(false);
                return Processor(await File.ReadAllBytesAsync(output, ct).ConfigureAwait(false));
            }
            finally
            {
                try
                {
                    Directory.Delete(work, recursive: true);
                }
                catch (IOException)
                {
                }
            }
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
