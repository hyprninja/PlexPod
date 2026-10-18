using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Models;
using RockPlex.Core.Tagging;
using RockPlex.Media.Art;
using RockPlex.Media.Ffmpeg;
using RockPlex.Plex.Library;

namespace RockPlex.Media.Pipeline;

public sealed record ExportRequest(
    string Folder,
    LibrarySnapshot Snapshot,
    IReadOnlyList<TrackItem> Tracks,
    IReadOnlyList<(PlaylistItem Playlist, PlaylistContents Contents)> Playlists,
    TagPolicy Policy);

public sealed record ExportProgress(int Done, int Total, string Current);

public sealed record ExportReport(int Succeeded, int Failed, int FromCache, long Bytes, string ReportPath);

/// <summary>
/// M2's only output: processes a selection into <c>&lt;folder&gt;\Music\…</c> on the PC, plus
/// <c>cover.jpg</c> files, test playlists and a report. Refuses any Rockbox drive: only M3's sync
/// engine may write to the iPod (manifest rule).
/// </summary>
public sealed class TestExportService(
    TrackPipeline pipeline,
    AlbumArtProvider art,
    ILogger<TestExportService> logger)
{
    public const string ReportFileName = "RockPlex-export.txt";

    /// <summary>Original downloads of tracks that failed, kept for diagnosis.</summary>
    public const string FailedFolderName = "_failed-originals";
    public const int TrackConcurrency = 2;

    /// <summary>True when <paramref name="folder"/> is on a drive whose root holds <c>.rockbox</c>.</summary>
    public static bool IsOnRockboxDrive(string folder) => IsOnRockboxDrive(folder, Directory.Exists);

    internal static bool IsOnRockboxDrive(string folder, Func<string, bool> directoryExists)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(folder));
        return !string.IsNullOrEmpty(root) && directoryExists(Path.Combine(root, ".rockbox"));
    }

    public async Task<ExportReport> ExportAsync(
        IPlexLibraryClient client,
        FfmpegInstall ffmpeg,
        ExportRequest request,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(request);

        if (IsOnRockboxDrive(request.Folder))
        {
            throw new InvalidOperationException("That folder is on the iPod. Test export only writes to the PC; copy the files across yourself.");
        }

        Directory.CreateDirectory(request.Folder);
        var lines = new ConcurrentQueue<(string Path, string Line)>();
        var exported = new ConcurrentDictionary<string, string>(StringComparer.Ordinal); // rating key → device path
        int done = 0, ok = 0, failed = 0, fromCache = 0;
        long bytes = 0;

        // Album-level data first: detail, artist genres (only when needed), art and cover.jpg.
        var albums = new Dictionary<string, AlbumContext>(StringComparer.Ordinal);
        var artists = new Dictionary<string, ArtistDetail?>(StringComparer.Ordinal);
        foreach (var albumKey in request.Tracks.Select(t => t.AlbumKey).Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var album = await client.GetAlbumDetailAsync(albumKey, ct).ConfigureAwait(false) ?? Fallback(request.Snapshot, albumKey);
            ArtistDetail? artist = null;
            if (request.Policy.Genre && album.Genres.Count == 0 && album.ArtistKey.Length > 0 && !artists.TryGetValue(album.ArtistKey, out artist))
            {
                artist = await client.GetArtistDetailAsync(album.ArtistKey, ct).ConfigureAwait(false);
                artists[album.ArtistKey] = artist;
            }

            var albumTracks = request.Snapshot.TracksByAlbum(albumKey).ToList();
            var images = await art.GetAsync(client, album, ct, ffmpeg).ConfigureAwait(false);
            albums[albumKey] = new AlbumContext(album, artist, albumTracks, images, albumTracks.Select(t => t.DiscIndex ?? 1).Distinct().Count() > 1);
        }

        // Device paths up front, so collisions within this export get a " (2)" suffix deterministically.
        var jobs = new List<TrackJob>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in request.Tracks.Where(t => t.IsAvailable))
        {
            var ctx = albums[track.AlbumKey];
            var ext = TrackPipeline.SourceExtension(track.Parts[0]);
            var year = ctx.Album.Year ?? YearOf(ctx.Album.OriginallyAvailableAt);
            var devicePath = DevicePathBuilder.Build(new PathFields(ctx.Album.ArtistTitle, year, ctx.Album.Title, track.DiscIndex, track.Index, track.Title), ext, ctx.IsMultiDisc);
            for (var n = 2; !used.Add(devicePath); n++)
            {
                devicePath = devicePath[..^ext.Length] + $" ({n})" + ext;
            }

            exported[track.RatingKey] = devicePath;
            jobs.Add(new TrackJob(track, ctx.Album, ctx.Artist, ctx.Tracks, null, ctx.Art, request.Policy, ToLocal(request.Folder, devicePath),
                Path.Combine(request.Folder, FailedFolderName)));
        }

        foreach (var (key, ctx) in albums)
        {
            if (ctx.Art is not null && jobs.FirstOrDefault(j => j.Track.AlbumKey == key) is { } first)
            {
                var folder = Path.GetDirectoryName(first.OutputPath)!;
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, "cover.jpg"), ctx.Art.CoverJpeg, ct).ConfigureAwait(false);
            }
        }

        var total = jobs.Count;
        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = TrackConcurrency, CancellationToken = ct },
            async (job, token) =>
            {
                var label = $"{job.Album.ArtistTitle} — {job.Track.Title}";
                progress?.Report(new ExportProgress(Volatile.Read(ref done), total, label));
                var devicePath = exported[job.Track.RatingKey];
                try
                {
                    if (request.Policy.MusicBrainzTrackId)
                    {
                        var detail = await client.GetTrackDetailAsync(job.Track.RatingKey, token).ConfigureAwait(false);
                        job = job with { MusicBrainzTrackId = detail?.MusicBrainzTrackId };
                    }

                    var result = await pipeline.ProcessAsync(client, ffmpeg, job, token).ConfigureAwait(false);
                    Interlocked.Increment(ref ok);
                    Interlocked.Add(ref bytes, result.SizeBytes);
                    if (result.FromCache)
                    {
                        Interlocked.Increment(ref fromCache);
                    }

                    var notes = (result.Tagging == Tags.TagWriteOutcome.NotRetagged ? ", NOT re-tagged (format)" : string.Empty)
                        + (result.FromCache ? ", conversion from cache" : string.Empty);
                    lines.Enqueue((devicePath, $"OK      {devicePath}  [{result.Action}{notes}]"));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failed);
                    exported.TryRemove(job.Track.RatingKey, out _);
                    logger.LogWarning("Export failed for {Track}: {Message}", label, ex.Message);
                    lines.Enqueue((devicePath, $"FAILED  {devicePath}  [{ex.Message}]"));
                }

                progress?.Report(new ExportProgress(Interlocked.Increment(ref done), total, label));
            }).ConfigureAwait(false);

        await WritePlaylistsAsync(request, exported, ct).ConfigureAwait(false);
        var reportPath = Path.Combine(request.Folder, ReportFileName);
        await File.WriteAllTextAsync(reportPath, BuildReport(request, ffmpeg, lines, ok, failed, fromCache, bytes), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new ExportReport(ok, failed, fromCache, bytes, reportPath);
    }

    /// <summary>
    /// Test playlists for the spike tests (M3 writes the real ones with their guarantees). Lines whose
    /// track failed or wasn't exported are left as comments, never as broken paths.
    /// </summary>
    private static async Task WritePlaylistsAsync(ExportRequest request, ConcurrentDictionary<string, string> exported, CancellationToken ct)
    {
        if (request.Playlists.Count == 0)
        {
            return;
        }

        var dir = Path.Combine(request.Folder, "Playlists");
        Directory.CreateDirectory(dir);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (playlist, contents) in request.Playlists)
        {
            var builder = new StringBuilder("#EXTM3U\n");
            builder.Append(CultureInfo.InvariantCulture, $"#ROCKPLEX ratingKey={playlist.RatingKey} test-export={DateTimeOffset.Now:O}\n");
            foreach (var entry in contents.Entries)
            {
                var track = entry.Track;
                if (track is not null && !entry.Unavailable && exported.TryGetValue(track.RatingKey, out var path))
                {
                    builder.Append(CultureInfo.InvariantCulture, $"#EXTINF:{track.DurationMs / 1000},{track.TrackArtist ?? string.Empty} - {track.Title}\n");
                    builder.Append(path).Append('\n');
                }
                else
                {
                    builder.Append(CultureInfo.InvariantCulture, $"# not exported: {track?.Title ?? "unavailable entry"}\n");
                }
            }

            var name = DevicePathBuilder.Sanitize(playlist.Title, "Playlist");
            var unique = name;
            for (var n = 2; !names.Add(unique); n++)
            {
                unique = $"{name} ({n})";
            }

            await File.WriteAllTextAsync(Path.Combine(dir, unique + ".m3u8"), builder.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        }
    }

    private static string BuildReport(ExportRequest request, FfmpegInstall ffmpeg, IEnumerable<(string Path, string Line)> lines, int ok, int failed, int fromCache, long bytes)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"RockPlex test export, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"{ok} exported, {failed} failed, {fromCache} conversions reused from cache, {bytes / 1048576.0:0.0} MB");
        builder.AppendLine(ffmpeg.Description);
        builder.AppendLine("Tags written: " + string.Join(", ", TagPolicy.Fields.Where(f => f.Get(request.Policy)).Select(f => f.DisplayName)));
        builder.AppendLine("Tags stripped: " + string.Join(", ", TagPolicy.Fields.Where(f => !f.Get(request.Policy)).Select(f => f.DisplayName)));
        builder.AppendLine("Always written: ROCKPLEX_RATINGKEY, ROCKPLEX_GUID, ROCKPLEX_PARTID");
        if (failed > 0)
        {
            builder.AppendLine($"Originals of failed tracks are in {FailedFolderName}\\ for inspection.");
        }
        builder.AppendLine();
        foreach (var (_, line) in lines.OrderBy(l => l.Path, StringComparer.Ordinal))
        {
            builder.AppendLine(line);
        }

        return builder.ToString();
    }

    private static string ToLocal(string folder, string devicePath) =>
        Path.Combine(folder, devicePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    private static int? YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    private static AlbumDetail Fallback(LibrarySnapshot snapshot, string albumKey)
    {
        var album = snapshot.FindAlbum(albumKey);
        return new AlbumDetail(albumKey, album?.Title ?? string.Empty, album?.ArtistKey ?? string.Empty, album?.ArtistTitle ?? string.Empty,
            album?.Year, null, [], [], null, album?.TrackCount ?? 0, album?.ThumbPath);
    }

    private sealed record AlbumContext(AlbumDetail Album, ArtistDetail? Artist, IReadOnlyList<TrackItem> Tracks, ArtImages? Art, bool IsMultiDisc);
}
