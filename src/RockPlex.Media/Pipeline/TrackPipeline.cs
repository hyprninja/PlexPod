using Microsoft.Extensions.Logging;
using RockPlex.Core.Media;
using RockPlex.Core.Models;
using RockPlex.Core.Tagging;
using RockPlex.Media.Art;
using RockPlex.Media.Ffmpeg;
using RockPlex.Media.Tags;
using RockPlex.Plex.Library;

namespace RockPlex.Media.Pipeline;

/// <summary>Everything needed to turn one Plex track into its iPod file.</summary>
public sealed record TrackJob(
    TrackItem Track,
    AlbumDetail Album,
    ArtistDetail? Artist,
    IReadOnlyList<TrackItem> AlbumTracks,
    string? MusicBrainzTrackId,
    ArtImages? Art,
    TagPolicy Policy,
    string OutputPath,
    string? FailedSourceFolder = null);

public sealed record ProcessedTrack(
    string OutputPath,
    string Action,
    TagWriteOutcome Tagging,
    string TagHash,
    long SizeBytes,
    bool FromCache,
    SourceTags SourceTags);

/// <summary>The track couldn't be processed; nothing was written to its output path.</summary>
public sealed class PipelineException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Download → decide from the real file → copy, convert or lossless re-encode → wipe and tag →
/// verify → move into place (M2 plan §4.5). Source files on the server are only ever downloaded.
/// </summary>
public sealed class TrackPipeline(
    MediaPaths paths,
    ConversionCache cache,
    FlacConverter converter,
    ContainerCleaner cleaner,
    AudioVerifier verifier,
    ILogger<TrackPipeline> logger)
{
    // One ffmpeg conversion at a time: it already uses several cores, and the disk is the bottleneck.
    private readonly SemaphoreSlim _conversions = new(1, 1);

    public async Task<ProcessedTrack> ProcessAsync(IPlexLibraryClient client, FfmpegInstall ffmpeg, TrackJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(job);

        var track = job.Track;
        var part = track.Parts.Count > 0 ? track.Parts[0] : throw new PipelineException("The track has no file on the server.");
        var work = Directory.CreateDirectory(Path.Combine(paths.Work, Guid.NewGuid().ToString("N"))).FullName;
        var ext = SourceExtension(part);
        var original = Path.Combine(work, "original" + ext);
        try
        {
            await DownloadAsync(client, part, original, ct).ConfigureAwait(false);

            var sourceTags = TagWriter.ReadSourceTags(original);
            var info = FlacFile.TryReadStreamInfo(original);
            FlacAction action = info is null ? FlacAction.Copy.Instance : FinalFormatPolicy.Decide(info);

            string staged;
            string? untaggedReference = null;
            var fromCache = false;
            if (action is FlacAction.Copy)
            {
                staged = Path.Combine(work, "out" + ext);
                if (ContainerCleaner.NeedsRemux(ext))
                {
                    await cleaner.RemuxAsync(ffmpeg, original, staged, ct).ConfigureAwait(false);
                }
                else
                {
                    File.Copy(original, staged);
                }
            }
            else
            {
                (untaggedReference, fromCache) = await ConvertAsync(ffmpeg, original, info!, part, track.UpdatedAt, action, ct).ConfigureAwait(false);
                staged = Path.Combine(work, "out.flac");
                File.Copy(untaggedReference, staged);
            }

            var tags = TagMapper.Map(new TagSource(track, job.Album, job.Artist, job.AlbumTracks, job.MusicBrainzTrackId, sourceTags), job.Policy);
            TagWriteOutcome outcome;
            try
            {
                outcome = TagWriter.Write(staged, tags, job.Art?.EmbeddedJpeg, TagWriter.ReadGaplessInfo(original));
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagWriteException or IOException)
            {
                throw new PipelineException($"Tagging failed: {ex.Message}", ex);
            }

            // Tagging must never touch the audio: compare against the untouched original (copies) or
            // the verified, untagged conversion.
            await verifier.VerifyCopyAsync(ffmpeg, untaggedReference ?? original, staged, ct).ConfigureAwait(false);

            MoveIntoPlace(staged, job.OutputPath);
            var size = new FileInfo(job.OutputPath).Length;
            return new ProcessedTrack(job.OutputPath, Describe(action, info, ext), outcome, tags.ContentHash(), size, fromCache, sourceTags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && KeepFailedSource(original, job))
        {
            throw; // never reached: KeepFailedSource only copies the file and returns false
        }
        catch (VerificationException ex)
        {
            throw new PipelineException($"Verification failed: {ex.Message}", ex);
        }
        catch (FfmpegException ex)
        {
            throw new PipelineException($"ffmpeg failed: {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new PipelineException(ex.Message, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PipelineException($"Download failed: {ex.Message}", ex);
        }
        catch (Plex.Http.PlexProtocolException ex)
        {
            throw new PipelineException(ex.Message, ex);
        }
        finally
        {
            TryDelete(work);
        }
    }

    public static string Describe(FlacAction action, FlacStreamInfo? info, string ext) => action switch
    {
        FlacAction.Convert c => $"converted {Format(info!.BitsPerSample, info.SampleRateHz)} → {Format(c.TargetBits, c.TargetRateHz)}",
        FlacAction.ReencodeLossless => $"re-encoded losslessly ({info!.MaxBlockSize}-sample blocks → {FinalFormatPolicy.OutputBlockSize})",
        _ when info is not null => $"copied (FLAC {Format(info.BitsPerSample, info.SampleRateHz)})",
        _ => $"copied ({ext.TrimStart('.')})",
    };

    private static string Format(int bits, int rate) =>
        $"{bits}/{(rate % 1000 == 0 ? (rate / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture) : (rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))}";

    private async Task<(string Path, bool FromCache)> ConvertAsync(
        FfmpegInstall ffmpeg, string original, FlacStreamInfo info, MediaPartRef part, long? updatedAt, FlacAction action, CancellationToken ct)
    {
        var key = ConversionCache.Key(part.PartId, updatedAt, action);
        var fromCache = cache.TryGet(key, out var cached);
        if (!fromCache)
        {
            await _conversions.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another worker may have produced it while we waited.
                fromCache = cache.TryGet(key, out cached);
                if (!fromCache)
                {
                    var temp = Path.Combine(Path.GetDirectoryName(original)!, "converted.flac");
                    logger.LogInformation("Converting part {PartId}: {Action}", part.PartId, Describe(action, info, ".flac"));
                    await converter.ConvertAsync(ffmpeg, original, temp, info, action, ct).ConfigureAwait(false);
                    await VerifyConversionAsync(ffmpeg, original, info, temp, action, ct).ConfigureAwait(false);
                    cached = cache.Add(key, temp);
                    return (cached, false);
                }
            }
            finally
            {
                _conversions.Release();
            }
        }

        // A cached file is re-checked against this download before it is trusted.
        await VerifyConversionAsync(ffmpeg, original, info, cached, action, ct).ConfigureAwait(false);
        return (cached, true);
    }

    private Task VerifyConversionAsync(FfmpegInstall ffmpeg, string original, FlacStreamInfo info, string converted, FlacAction action, CancellationToken ct) =>
        action switch
        {
            FlacAction.Convert c => verifier.VerifyConversionAsync(ffmpeg, info, converted, c, ct),
            _ => verifier.VerifyLosslessAsync(ffmpeg, original, converted, ct),
        };

    /// <summary>
    /// Exception filter (runs before the work folder is deleted): keeps the original download of a
    /// failed track for inspection. Always returns false, so the exception carries on unchanged.
    /// </summary>
    private bool KeepFailedSource(string original, TrackJob job)
    {
        if (job.FailedSourceFolder is null || !File.Exists(original))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(job.FailedSourceFolder);
            var name = DevicePathBuilder.Sanitize($"{job.Track.RatingKey} - {job.Album.ArtistTitle} - {job.Track.Title}", job.Track.RatingKey) + Path.GetExtension(original);
            File.Copy(original, Path.Combine(job.FailedSourceFolder, name), overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug("Couldn't keep failed source: {Message}", ex.Message);
        }

        return false;
    }

    private static async Task DownloadAsync(IPlexLibraryClient client, MediaPartRef part, string path, CancellationToken ct)
    {
        var temp = path + ".part";
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            await client.DownloadPartAsync(part, stream, null, ct).ConfigureAwait(false);
        }

        File.Move(temp, path);
    }

    /// <summary>Temp name then rename, so a crash never leaves a half-written file under the final name.</summary>
    private static void MoveIntoPlace(string staged, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temp = output + ".rockplex-tmp";
        File.Move(staged, temp, overwrite: true);
        File.Move(temp, output, overwrite: true);
    }

    internal static string SourceExtension(MediaPartRef part)
    {
        var fromKey = Path.GetExtension(part.Key);
        if (fromKey.Length is > 1 and <= 6)
        {
            return fromKey.ToLowerInvariant();
        }

        return string.IsNullOrEmpty(part.Container) ? ".bin" : "." + part.Container.ToLowerInvariant();
    }

    private void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            logger.LogDebug("Couldn't clear {Dir}: {Message}", dir, ex.Message);
        }
    }
}
