using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Media;
using RockPlex.Core.Models;
using RockPlex.Core.Sync;
using RockPlex.Core.Tagging;
using RockPlex.Device.Sync;
using RockPlex.Media.Art;
using RockPlex.Media.Pipeline;

namespace RockPlex.Media.Sync;

/// <summary>
/// Makes the iPod's RockPlex content match the selection (M3 plan §5), in the mandated order:
/// [make room] → moves → tracks (produce, copy, read back, rename) → playlists → line check (P6) →
/// removals last → commit. Only files in the manifest are ever changed or deleted.
/// </summary>
public sealed class SyncEngine(
    TrackPipeline pipeline,
    AlbumArtProvider art,
    MediaPaths paths,
    DeviceFileOps files,
    ILogger<SyncEngine> logger)
{
    /// <summary>Allowance per track for tags and embedded art written on top of the audio.</summary>
    public const long TagAllowanceBytes = 256 * 1024;

    public const string CoverFileName = "cover.jpg";
    public const string SelectionFileName = "selection.json";
    public const string ReportFileName = "last-sync.txt";

    private const int ProducerConcurrency = 2;

    /// <summary>Free space on the device; replaceable in tests to exercise the fit gate and make-room mode.</summary>
    internal Func<string, long> FreeSpace { get; init; } = DeviceFileOps.FreeBytes;
    private const int SaveEveryFiles = 20;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ---------------------------------------------------------------- preview (read-only)

    public async Task<SyncPreview> PreviewAsync(SyncRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = request.DriveRoot;
        var store = new DeviceStore(root);
        var identity = await store.TryReadIdentityAsync(ct).ConfigureAwait(false);
        var (loaded, source) = await store.LoadManifestAsync(ct).ConfigureAwait(false);
        var manifest = loaded ?? DeviceManifest.Empty(identity?.DeviceId ?? string.Empty);

        var listing = await Task.Run(() => DeviceFileOps.ListFiles(root, "Music", "Playlists"), ct).ConfigureAwait(false);
        var owned = OwnedPaths(manifest);
        var unowned = listing.Where(f => !owned.Contains(f)).ToList();
        var adoption = await Task.Run(() => Adopter.Scan(root, unowned, ct), ct).ConfigureAwait(false);

        var blockers = new List<string>();
        var targets = BuildTargets(request);
        var targetByKey = targets.ToDictionary(t => t.Track.RatingKey, StringComparer.Ordinal);

        // Adopted files don't record the part key; fill it in where the part id still matches Plex.
        var adoptedTracks = adoption.Tracks
            .Select(a => targetByKey.TryGetValue(a.RatingKey, out var t) && t.Part.PartId == a.PartId
                ? a with { PartKey = t.Part.Key, SourceSizeBytes = t.Part.SizeBytes }
                : a)
            .ToList();
        manifest = manifest with
        {
            Tracks = [.. manifest.Tracks, .. adoptedTracks],
            Playlists = [.. manifest.Playlists, .. adoption.Playlists],
            Extras = [.. manifest.Extras, .. adoption.Extras],
            Folders = [.. manifest.Folders.Union(adoption.Folders, StringComparer.OrdinalIgnoreCase)],
        };

        var nowOwned = OwnedPaths(manifest);
        var userFiles = listing.Where(f => !nowOwned.Contains(f)).ToList();

        var playlists = new List<SyncPlaylistTarget>();
        foreach (var key in request.Selection.PlaylistKeys)
        {
            var playlist = request.Snapshot.FindPlaylist(key);
            if (playlist is not null && request.PlaylistContents.TryGetValue(key, out var contents))
            {
                playlists.Add(new SyncPlaylistTarget(playlist, contents));
            }
            else
            {
                blockers.Add($"Playlist \"{playlist?.Title ?? key}\" hasn't finished loading from Plex yet.");
            }
        }

        if (request.Selection.PendingPlaylistKeys.Count > 0 && blockers.Count == 0)
        {
            blockers.Add("Some selected playlists are still loading from Plex.");
        }

        var plan = SyncPlanner.Plan(manifest, targets, playlists, userFiles, t => ArtistOf(request.Snapshot, t));
        var fit = FitCheck.Evaluate(plan, FreeSpace(root));
        if (fit.Outcome == FitOutcome.TooBig)
        {
            blockers.Add($"The selection doesn't fit: {Mb(fit.OverBy)} MB over, even after removals.");
        }

        return new SyncPreview(request, identity, manifest, source, adoption, plan, fit, blockers);
    }

    // ---------------------------------------------------------------- execute

    public async Task<SyncResult> ExecuteAsync(SyncPreview preview, IProgress<SyncProgress>? progress, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var run = new Run(preview, art, progress, stop);
        try
        {
            if (!preview.CanStart)
            {
                run.Failures.AddRange(preview.Blockers);
                return run.Finish(completed: false, logger);
            }

            await PreflightAsync(run).ConfigureAwait(false);
            if (preview.UsesMakeRoom)
            {
                await MakeRoomAsync(run).ConfigureAwait(false);
            }

            await MovesAsync(run).ConfigureAwait(false);
            await TracksAsync(run).ConfigureAwait(false);
            if (run.Stopped)
            {
                await run.SaveAsync().ConfigureAwait(false);
                run.Warnings.Add("Stopped before playlists and removals. The iPod is consistent; sync again to finish.");
                return await CommitAsync(run, completed: false).ConfigureAwait(false);
            }

            await CoversAsync(run).ConfigureAwait(false);
            await PlaylistsAsync(run).ConfigureAwait(false);

            if (!VerifyPlaylists(run))
            {
                await run.SaveAsync().ConfigureAwait(false);
                return await CommitAsync(run, completed: false).ConfigureAwait(false);
            }

            // Removals last (P5). Stale playlists go first: they are being removed anyway, and the
            // referenced-paths guard in RemoveTracksAsync must not count them.
            await RemovePlaylistsAsync(run, run.Preview.Plan.PlaylistRemovals).ConfigureAwait(false);
            if (!preview.UsesMakeRoom)
            {
                await RemoveTracksAsync(run, run.Preview.Plan.TrackRemovals).ConfigureAwait(false);
            }
            await RemoveOrphanCoversAndFoldersAsync(run).ConfigureAwait(false);
            return await CommitAsync(run, completed: run.Failures.Count == 0).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !Directory.Exists(preview.Request.DriveRoot))
        {
            run.Failures.Add("The iPod was disconnected. Nothing is broken: plug it back in and sync again to finish.");
            logger.LogWarning(ex, "Device disappeared during sync");
            return run.Finish(completed: false, logger);
        }
    }

    private async Task PreflightAsync(Run run)
    {
        var request = run.Preview.Request;
        run.Report("Preparing", 0, 0, request.DriveRoot);
        var identity = await run.Store.LoadOrCreateIdentityAsync(request.VolumeLabel, request.RockboxTarget, CancellationToken.None).ConfigureAwait(false);
        if (run.Preview.Identity is { } seen && seen.DeviceId != identity.DeviceId)
        {
            throw new InvalidOperationException("A different iPod is connected than the one that was planned for. Plan the sync again.");
        }

        run.State.DeviceId = identity.DeviceId;
        var cleaned = DeviceFileOps.CleanTempFiles(request.DriveRoot);
        if (cleaned > 0)
        {
            run.Warnings.Add($"Removed {cleaned} unfinished temporary file(s) from an interrupted sync.");
        }

        if (run.Preview.Adoption.Tracks.Count + run.Preview.Adoption.Playlists.Count > 0)
        {
            run.Warnings.Add($"Adopted {run.Preview.Adoption.Tracks.Count} track(s) and {run.Preview.Adoption.Playlists.Count} playlist(s) RockPlex had written before.");
        }

        if (run.Preview.ManifestSource == ManifestSource.Previous)
        {
            run.Warnings.Add("The manifest was damaged; recovered from the previous copy.");
        }

        await run.SaveAsync().ConfigureAwait(false); // persists adoption and the device id before any change
    }

    /// <summary>D5: free space first without ever leaving a broken playlist line (M3 plan §5a).</summary>
    private async Task MakeRoomAsync(Run run)
    {
        var plan = run.Preview.Plan;
        var affected = plan.PlaylistOps.Where(p => p.NeedsWrite && p.Existing is not null).Select(p => p.Existing!)
            .Concat(plan.PlaylistRemovals)
            .ToList();
        await RemovePlaylistsAsync(run, affected).ConfigureAwait(false);
        await run.SaveAsync().ConfigureAwait(false);
        await RemoveTracksAsync(run, plan.TrackRemovals).ConfigureAwait(false);
        await run.SaveAsync().ConfigureAwait(false);
    }

    private async Task MovesAsync(Run run)
    {
        var moves = run.Preview.Plan.TrackOps.Where(o => o.Kind == TrackOpKind.Move).ToList();
        var done = 0;
        foreach (var op in moves)
        {
            var existing = run.State.Tracks.GetValueOrDefault(op.Existing!.DevicePath) ?? op.Existing;
            run.Report("Moving", done, moves.Count, op.DevicePath);
            if (!string.Equals(existing.DevicePath, op.DevicePath, StringComparison.Ordinal))
            {
                foreach (var folder in files.Move(run.Root, existing.DevicePath, op.DevicePath))
                {
                    run.State.Folders.Add(folder);
                }

                run.Moved++;
            }

            run.State.Tracks.Remove(existing.DevicePath);
            var moved = existing with
            {
                DevicePath = op.DevicePath,
                DesiredPath = op.Target.DesiredPath,
                MetadataStamp = op.AlsoRetag ? existing.MetadataStamp : op.Target.MetadataStamp,
            };
            run.State.Tracks[op.DevicePath] = moved;
            done++;
            await run.SaveIfDueAsync().ConfigureAwait(false);
        }
    }

    private async Task TracksAsync(Run run)
    {
        var request = run.Preview.Request;
        var ops = run.Preview.Plan.TrackOps;
        var work = new List<TrackOp>(ops.Where(o => o.Kind is TrackOpKind.Add or TrackOpKind.Replace));

        // Re-tag candidates: derive the tags they would get now; only rewrite when they differ.
        var candidates = ops.Where(o => o.Kind == TrackOpKind.Retag || (o.Kind == TrackOpKind.Move && o.AlsoRetag)).ToList();
        var checkedCount = 0;
        foreach (var op in candidates)
        {
            run.CheckStop();
            if (run.Stopped)
            {
                return;
            }

            run.Report("Checking tags", checkedCount++, candidates.Count, op.Target.Track.Title);
            var existing = run.State.Tracks[op.DevicePath];
            var album = await run.AlbumAsync(op.Target.Track.AlbumKey).ConfigureAwait(false);
            var artist = await run.ArtistForGenreAsync(album).ConfigureAwait(false);
            var mbid = request.Policy.MusicBrainzTrackId ? await run.MbidAsync(op.Target.Track.RatingKey).ConfigureAwait(false) : null;
            var expected = TagMapper.Map(
                new TagSource(op.Target.Track, album, artist, request.Snapshot.TracksByAlbum(op.Target.Track.AlbumKey).ToList(), mbid, new SourceTags(existing.SourceComposer, existing.SourceComment)),
                request.Policy).ContentHash();
            if (expected == existing.TagHash)
            {
                run.State.Tracks[op.DevicePath] = existing with { MetadataStamp = op.Target.MetadataStamp, MusicBrainzTrackId = mbid ?? existing.MusicBrainzTrackId };
                run.TagsAlreadyCurrent++;
            }
            else
            {
                work.Add(op);
            }
        }

        if (work.Count == 0)
        {
            return;
        }

        var staging = Directory.CreateDirectory(Path.Combine(paths.CacheRoot, "sync-staging")).FullName;
        var channel = Channel.CreateBounded<(TrackOp Op, ProcessedTrack Result)>(new BoundedChannelOptions(ProducerConcurrency) { SingleReader = true });
        using var produceCts = CancellationTokenSource.CreateLinkedTokenSource(run.Stop);
        var total = work.Count;
        var done = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    work,
                    new ParallelOptions { MaxDegreeOfParallelism = ProducerConcurrency, CancellationToken = produceCts.Token },
                    async (op, token) =>
                    {
                        try
                        {
                            var processed = await ProduceAsync(run, op, staging, token).ConfigureAwait(false);
                            await channel.Writer.WriteAsync((op, processed), token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is PipelineException or HttpRequestException or Plex.Http.PlexProtocolException
                            or IOException && Directory.Exists(run.Root))
                        {
                            run.Fail(op, ex.Message);
                        }
                    }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stop pressed (or the consumer gave up): stop producing.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        });

        try
        {
            await foreach (var (op, processed) in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (run.Stop.IsCancellationRequested)
                    {
                        run.Stopped = true;
                        continue; // drain: discard anything produced after Stop
                    }

                    run.Report("Copying to iPod", done, total, op.DevicePath);
                    await CommitTrackAsync(run, op, processed).ConfigureAwait(false);
                    done++;
                    await run.SaveIfDueAsync().ConfigureAwait(false);
                }
                catch (DeviceVerifyException ex)
                {
                    run.Fail(op, ex.Message);
                }
                finally
                {
                    TryDelete(processed.OutputPath);
                }
            }
        }
        catch
        {
            await produceCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await producer.ConfigureAwait(false);
        }

        if (run.Stop.IsCancellationRequested)
        {
            run.Stopped = true;
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    private async Task<ProcessedTrack> ProduceAsync(Run run, TrackOp op, string staging, CancellationToken ct)
    {
        var request = run.Preview.Request;
        var track = op.Target.Track;
        var album = await run.AlbumAsync(track.AlbumKey).ConfigureAwait(false);
        var artist = await run.ArtistForGenreAsync(album).ConfigureAwait(false);
        var mbid = request.Policy.MusicBrainzTrackId ? await run.MbidAsync(track.RatingKey).ConfigureAwait(false) : null;
        var images = await run.ArtAsync(album).ConfigureAwait(false);
        var output = Path.Combine(staging, Guid.NewGuid().ToString("N") + TrackPipeline.SourceExtension(track.Parts[0]));
        var job = new TrackJob(track, album, artist, request.Snapshot.TracksByAlbum(track.AlbumKey).ToList(), mbid, images, request.Policy, output);
        return await pipeline.ProcessAsync(request.Client, request.Ffmpeg, job, ct).ConfigureAwait(false);
    }

    /// <summary>Copy → read back → rename (D6), then record the file as owned.</summary>
    private async Task CommitTrackAsync(Run run, TrackOp op, ProcessedTrack processed)
    {
        // Never abandon a device write half-way: Stop takes effect between files.
        var copy = await files.CopyVerifiedAsync(processed.OutputPath, run.Root, op.DevicePath, CancellationToken.None).ConfigureAwait(false);
        foreach (var folder in copy.CreatedFolders)
        {
            run.State.Folders.Add(folder);
        }

        var part = op.Target.Part;
        run.State.Tracks[op.DevicePath] = new ManifestTrack
        {
            DevicePath = op.DevicePath,
            DesiredPath = op.Target.DesiredPath,
            RatingKey = op.Target.Track.RatingKey,
            Guid = op.Target.Track.Guid,
            PartId = part.PartId,
            PartKey = part.Key,
            SourceSizeBytes = part.SizeBytes,
            SizeBytes = copy.SizeBytes,
            Sha256 = copy.Sha256,
            TagHash = processed.TagHash,
            MetadataStamp = op.Target.MetadataStamp,
            SourceComposer = processed.SourceTags.Composer,
            SourceComment = processed.SourceTags.Comment,
            MusicBrainzTrackId = run.Mbids.GetValueOrDefault(op.Target.Track.RatingKey),
            Action = processed.Action,
        };
        run.BytesWritten += copy.SizeBytes;
        run.AlbumsTouched.Add(op.Target.Track.AlbumKey);
        switch (op.Kind)
        {
            case TrackOpKind.Add:
                run.Added++;
                break;
            case TrackOpKind.Replace:
                run.Replaced++;
                break;
            default:
                run.Retagged++;
                break;
        }
    }

    /// <summary>A <c>cover.jpg</c> in every album folder RockPlex fills (PictureFlow), never replacing the user's own.</summary>
    private async Task CoversAsync(Run run)
    {
        var userFiles = DeviceFileOps.ListFiles(run.Root, "Music").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = run.State.Tracks.Values
            .GroupBy(t => Parent(t.DevicePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Folder: g.Key, Track: g.First()))
            .ToList();
        foreach (var (folder, sample) in folders)
        {
            var coverPath = folder + "/" + CoverFileName;
            var extra = run.State.Extras.GetValueOrDefault(coverPath);
            if (extra is null && userFiles.Contains(coverPath))
            {
                continue; // the user's own cover.jpg
            }

            var albumKey = run.Preview.Request.Snapshot.FindTrack(sample.RatingKey)?.AlbumKey;
            if (albumKey is null || (extra is not null && !run.AlbumsTouched.Contains(albumKey)))
            {
                continue;
            }

            var images = await run.ArtAsync(await run.AlbumAsync(albumKey).ConfigureAwait(false)).ConfigureAwait(false);
            if (images is null)
            {
                continue;
            }

            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(images.CoverJpeg));
            if (extra is not null && extra.Sha256 == sha)
            {
                continue;
            }

            var temp = Path.Combine(paths.CacheRoot, "sync-staging", Guid.NewGuid().ToString("N") + ".jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await File.WriteAllBytesAsync(temp, images.CoverJpeg).ConfigureAwait(false);
            try
            {
                var copy = await files.CopyVerifiedAsync(temp, run.Root, coverPath, CancellationToken.None).ConfigureAwait(false);
                run.State.Extras[coverPath] = new ManifestExtra(coverPath, "cover", copy.Sha256, folder);
            }
            catch (DeviceVerifyException ex)
            {
                run.Warnings.Add($"Album art for {folder}: {ex.Message}");
            }
            finally
            {
                TryDelete(temp);
            }
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    private async Task PlaylistsAsync(Run run)
    {
        var request = run.Preview.Request;
        var pathByKey = run.State.Tracks.Values
            .GroupBy(t => t.RatingKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().DevicePath, StringComparer.Ordinal);
        var ops = run.Preview.Plan.PlaylistOps;
        var done = 0;
        foreach (var op in ops)
        {
            run.Report("Writing playlists", done++, ops.Count, op.Target.Playlist.Title);

            // P1: a playlist is never written partially. If any of its available tracks failed this
            // sync, the previous version stays (its tracks are all still on the iPod).
            var missing = op.Target.Contents.Entries
                .Where(e => !e.Unavailable && e.Track is { IsAvailable: true } t && !pathByKey.ContainsKey(t.RatingKey))
                .Select(e => e.Track!.Title)
                .Distinct()
                .ToList();
            if (missing.Count > 0)
            {
                run.Failures.Add($"Playlist \"{op.Target.Playlist.Title}\" not updated: {missing.Count} of its tracks didn't copy ({string.Join(", ", missing.Take(3))}).");
                continue;
            }

            var (content, skipped) = PlaylistWriter.Build(op.Target, pathByKey, t => ArtistOf(request.Snapshot, t));
            if (skipped.Count > 0)
            {
                run.Warnings.Add($"Playlist \"{op.Target.Playlist.Title}\": {skipped.Count} entr{(skipped.Count == 1 ? "y" : "ies")} skipped (file missing on the Plex server).");
            }

            var hash = PlaylistWriter.Hash(content);
            if (run.State.Playlists.GetValueOrDefault(op.DevicePath) is { } current && current.ContentHash == hash && current.RatingKey == op.Target.Playlist.RatingKey)
            {
                continue;
            }

            var copy = await files.WriteTextVerifiedAsync(content, run.Root, op.DevicePath, CancellationToken.None).ConfigureAwait(false);
            foreach (var folder in copy.CreatedFolders)
            {
                run.State.Folders.Add(folder);
            }

            run.State.Playlists[op.DevicePath] = new ManifestPlaylist(op.Target.Playlist.RatingKey, op.DevicePath, op.Target.Playlist.Title, hash);
            run.PlaylistsWritten++;
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    /// <summary>P6: every RockPlex playlist re-read from the iPod, every line resolved with exact case.</summary>
    private bool VerifyPlaylists(Run run)
    {
        run.Report("Checking playlists", 0, 1, string.Empty);
        var problems = new PlaylistVerifier().Verify(run.Root, run.State.Playlists.Keys);
        if (problems.Count == 0)
        {
            return true;
        }

        run.Failures.Add($"Playlist check failed ({problems.Count} line(s)); nothing was removed:");
        run.Failures.AddRange(problems.Take(20).Select(p => "  " + p));
        return false;
    }

    private async Task RemoveTracksAsync(Run run, IReadOnlyList<ManifestTrack> removals)
    {
        if (removals.Count == 0)
        {
            return;
        }

        // Belt and braces for P5: never delete a file any RockPlex playlist on the iPod still names.
        var referenced = ReferencedPaths(run);
        var done = 0;
        foreach (var removal in removals)
        {
            run.Report("Removing", done++, removals.Count, removal.DevicePath);
            if (!run.State.Tracks.TryGetValue(removal.DevicePath, out var owned) || owned.RatingKey != removal.RatingKey)
            {
                continue; // already gone, or the path now belongs to another track
            }

            if (referenced.Contains(removal.DevicePath))
            {
                run.Warnings.Add($"Kept {removal.DevicePath}: a playlist on the iPod still uses it.");
                continue;
            }

            DeviceFileOps.Delete(run.Root, removal.DevicePath);
            run.State.Tracks.Remove(removal.DevicePath);
            run.Removed++;
            await run.SaveIfDueAsync().ConfigureAwait(false);
        }
    }

    private async Task RemovePlaylistsAsync(Run run, IReadOnlyList<ManifestPlaylist> removals)
    {
        foreach (var removal in removals)
        {
            if (run.State.Playlists.Remove(removal.DevicePath))
            {
                DeviceFileOps.Delete(run.Root, removal.DevicePath);
                run.PlaylistsRemoved++;
            }
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    private async Task RemoveOrphanCoversAndFoldersAsync(Run run)
    {
        var trackFolders = run.State.Tracks.Values.Select(t => Parent(t.DevicePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var cover in run.State.Extras.Values.Where(e => !trackFolders.Contains(Parent(e.DevicePath))).ToList())
        {
            DeviceFileOps.Delete(run.Root, cover.DevicePath);
            run.State.Extras.Remove(cover.DevicePath);
        }

        var removable = run.State.Folders.Where(f => !IsTopLevel(f)).ToList();
        foreach (var folder in DeviceFileOps.DeleteEmptyFolders(run.Root, removable))
        {
            run.State.Folders.Remove(folder);
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    private async Task<SyncResult> CommitAsync(Run run, bool completed)
    {
        run.Report("Finishing", 0, 0, string.Empty);
        await run.SaveAsync().ConfigureAwait(false);
        await run.Store.WriteTextAsync(SelectionFileName, JsonSerializer.Serialize(run.Preview.Request.Profile, Json), CancellationToken.None).ConfigureAwait(false);
        var result = run.Finish(completed, logger);
        await run.Store.WriteTextAsync(ReportFileName, result.Report, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    private static HashSet<string> ReferencedPaths(Run run)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in run.State.Playlists.Keys)
        {
            var local = DevicePaths.ToLocal(run.Root, playlist);
            if (File.Exists(local))
            {
                referenced.UnionWith(PlaylistWriter.TrackLines(File.ReadAllText(local)).Select(l => l.Path));
            }
        }

        return referenced;
    }

    // ---------------------------------------------------------------- helpers

    internal static List<SyncTrackTarget> BuildTargets(SyncRequest request)
    {
        var snapshot = request.Snapshot;
        var fromPlaylists = request.PlaylistContents.Values
            .SelectMany(c => c.Entries)
            .Where(e => e.Track is not null)
            .Select(e => e.Track!)
            .DistinctBy(t => t.RatingKey)
            .ToDictionary(t => t.RatingKey, StringComparer.Ordinal);
        var fingerprint = request.Policy.Fingerprint();
        var targets = new List<SyncTrackTarget>();
        foreach (var key in request.Selection.TrackKeys)
        {
            var track = snapshot.FindTrack(key) ?? fromPlaylists.GetValueOrDefault(key);
            if (track is not { IsAvailable: true })
            {
                continue;
            }

            var album = snapshot.FindAlbum(track.AlbumKey);
            var multiDisc = snapshot.TracksByAlbum(track.AlbumKey).Select(t => t.DiscIndex ?? 1).Distinct().Count() > 1;
            var path = DevicePathBuilder.Build(
                new PathFields(album?.ArtistTitle ?? ArtistOf(snapshot, track), album?.Year, album?.Title, track.DiscIndex, track.Index, track.Title),
                TrackPipeline.SourceExtension(track.Parts[0]),
                multiDisc);
            var estimate = request.KnownSizes.TryGetValue(key, out var known)
                ? known
                : SizeEstimator.EstimateBytes(track, request.FormatOverrides.GetValueOrDefault(key) ?? track.Format).Bytes;
            targets.Add(new SyncTrackTarget(track, path, estimate + TagAllowanceBytes, SyncTrackTarget.Stamp(track.UpdatedAt, album?.UpdatedAt, fingerprint)));
        }

        return targets;
    }

    internal static string ArtistOf(LibrarySnapshot snapshot, TrackItem track) =>
        track.TrackArtist
        ?? snapshot.FindAlbum(track.AlbumKey)?.ArtistTitle
        ?? snapshot.FindArtist(track.ArtistKey)?.Title
        ?? "Unknown Artist";

    private static HashSet<string> OwnedPaths(DeviceManifest manifest) =>
        manifest.Tracks.Select(t => t.DevicePath)
            .Concat(manifest.Playlists.Select(p => p.DevicePath))
            .Concat(manifest.Extras.Select(e => e.DevicePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Parent(string devicePath) => devicePath[..Math.Max(0, devicePath.LastIndexOf('/'))];

    private static bool IsTopLevel(string folder) => folder.Count(c => c == '/') <= 1;

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Mutable state of one sync run.</summary>
    private sealed class Run
    {
        private readonly AlbumArtProvider _artProvider;
        private readonly IProgress<SyncProgress>? _progress;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<string, AlbumDetail> _albums = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ArtistDetail?> _artists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ArtImages?> _art = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _lookups = new(1, 1);
        private DateTime _lastSave = DateTime.UtcNow;
        private int _unsaved;

        public Run(SyncPreview preview, AlbumArtProvider artProvider, IProgress<SyncProgress>? progress, CancellationToken stop)
        {
            Preview = preview;
            _artProvider = artProvider;
            _progress = progress;
            Stop = stop;
            Store = new DeviceStore(preview.Request.DriveRoot);
            State = new MutableManifest(preview.Manifest);
        }

        public SyncPreview Preview { get; }

        public CancellationToken Stop { get; }

        public DeviceStore Store { get; }

        public MutableManifest State { get; }

        public string Root => Preview.Request.DriveRoot;

        public List<string> Failures { get; } = [];

        public List<string> Warnings { get; } = [];

        public HashSet<string> AlbumsTouched { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string?> Mbids { get; } = new(StringComparer.Ordinal);

        public bool Stopped { get; set; }

        public int Added { get; set; }

        public int Replaced { get; set; }

        public int Retagged { get; set; }

        public int TagsAlreadyCurrent { get; set; }

        public int Moved { get; set; }

        public int Removed { get; set; }

        public int PlaylistsWritten { get; set; }

        public int PlaylistsRemoved { get; set; }

        public long BytesWritten { get; set; }

        public void Report(string phase, int done, int total, string current) =>
            _progress?.Report(new SyncProgress(phase, done, total, current, BytesWritten));

        public void CheckStop()
        {
            if (Stop.IsCancellationRequested)
            {
                Stopped = true;
            }
        }

        public void Fail(TrackOp op, string reason)
        {
            lock (Failures)
            {
                Failures.Add($"{op.DevicePath}: {reason}");
            }
        }

        public async Task SaveAsync()
        {
            await Store.SaveManifestAsync(State.ToManifest(), CancellationToken.None).ConfigureAwait(false);
            _lastSave = DateTime.UtcNow;
            _unsaved = 0;
        }

        public Task SaveIfDueAsync() =>
            ++_unsaved >= SaveEveryFiles || DateTime.UtcNow - _lastSave > SaveEvery ? SaveAsync() : Task.CompletedTask;

        public async Task<AlbumDetail> AlbumAsync(string albumKey)
        {
            await _lookups.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_albums.TryGetValue(albumKey, out var album))
                {
                    album = await Preview.Request.Client.GetAlbumDetailAsync(albumKey, Stop).ConfigureAwait(false) ?? Fallback(albumKey);
                    _albums[albumKey] = album;
                }

                return album;
            }
            finally
            {
                _lookups.Release();
            }
        }

        public async Task<ArtistDetail?> ArtistForGenreAsync(AlbumDetail album)
        {
            if (!Preview.Request.Policy.Genre || album.Genres.Count > 0 || album.ArtistKey.Length == 0)
            {
                return null;
            }

            await _lookups.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_artists.TryGetValue(album.ArtistKey, out var artist))
                {
                    artist = await Preview.Request.Client.GetArtistDetailAsync(album.ArtistKey, Stop).ConfigureAwait(false);
                    _artists[album.ArtistKey] = artist;
                }

                return artist;
            }
            finally
            {
                _lookups.Release();
            }
        }

        public async Task<string?> MbidAsync(string ratingKey)
        {
            var detail = await Preview.Request.Client.GetTrackDetailAsync(ratingKey, Stop).ConfigureAwait(false);
            lock (Mbids)
            {
                Mbids[ratingKey] = detail?.MusicBrainzTrackId;
            }

            return detail?.MusicBrainzTrackId;
        }

        public SyncResult Finish(bool completed, ILogger logger)
        {
            var report = BuildReport(completed);
            logger.LogInformation("Sync finished: completed={Completed} added={Added} replaced={Replaced} retagged={Retagged} moved={Moved} removed={Removed} playlists={Playlists} failures={Failures}",
                completed, Added, Replaced, Retagged, Moved, Removed, PlaylistsWritten, Failures.Count);
            return new SyncResult(completed, Stopped, Added, Replaced, Retagged, TagsAlreadyCurrent, Moved, Removed, PlaylistsWritten, PlaylistsRemoved,
                BytesWritten, _clock.Elapsed, [.. Failures], [.. Warnings], report);
        }

        public async Task<ArtImages?> ArtAsync(AlbumDetail album)
        {
            await _lookups.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_art.TryGetValue(album.RatingKey, out var images))
                {
                    images = await _artProvider.GetAsync(Preview.Request.Client, album, Stop).ConfigureAwait(false);
                    _art[album.RatingKey] = images;
                }

                return images;
            }
            finally
            {
                _lookups.Release();
            }
        }

        private AlbumDetail Fallback(string albumKey)
        {
            var album = Preview.Request.Snapshot.FindAlbum(albumKey);
            return new AlbumDetail(albumKey, album?.Title ?? string.Empty, album?.ArtistKey ?? string.Empty, album?.ArtistTitle ?? string.Empty,
                album?.Year, null, [], [], null, album?.TrackCount ?? 0, album?.ThumbPath);
        }

        private string BuildReport(bool completed)
        {
            var b = new StringBuilder();
            b.AppendLine(CultureInfo.InvariantCulture, $"RockPlex sync, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz} ({_clock.Elapsed:hh\\:mm\\:ss})");
            b.AppendLine(completed ? "Result: completed" : Stopped ? "Result: stopped (safe to sync again)" : "Result: finished with problems");
            b.AppendLine(CultureInfo.InvariantCulture, $"Added {Added}, replaced {Replaced}, re-tagged {Retagged} ({TagsAlreadyCurrent} already current), moved {Moved}, removed {Removed}.");
            b.AppendLine(CultureInfo.InvariantCulture, $"Playlists written {PlaylistsWritten}, removed {PlaylistsRemoved}. {BytesWritten / 1048576.0:0.0} MB written, every file read back and verified.");
            if (Failures.Count > 0)
            {
                b.AppendLine().AppendLine("Problems:");
                Failures.ForEach(f => b.AppendLine("  " + f));
            }

            if (Warnings.Count > 0)
            {
                b.AppendLine().AppendLine("Notes:");
                Warnings.ForEach(w => b.AppendLine("  " + w));
            }

            b.AppendLine().AppendLine("Run Database → Update Now on the iPod (or keep Auto Update on) so the new tracks appear in its menus.");
            return b.ToString();
        }
    }

    /// <summary>The manifest as dictionaries while a sync edits it; snapshotted on every save.</summary>
    private sealed class MutableManifest(DeviceManifest source)
    {
        public string DeviceId { get; set; } = source.DeviceId;

        public Dictionary<string, ManifestTrack> Tracks { get; } = source.Tracks
            .GroupBy(t => t.DevicePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ManifestPlaylist> Playlists { get; } = source.Playlists
            .GroupBy(p => p.DevicePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, ManifestExtra> Extras { get; } = source.Extras
            .GroupBy(e => e.DevicePath, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Folders { get; } = new(source.Folders, StringComparer.OrdinalIgnoreCase);

        public DeviceManifest ToManifest() => new()
        {
            DeviceId = DeviceId,
            Tracks = [.. Tracks.Values.OrderBy(t => t.DevicePath, StringComparer.Ordinal)],
            Playlists = [.. Playlists.Values.OrderBy(p => p.DevicePath, StringComparer.Ordinal)],
            Extras = [.. Extras.Values.OrderBy(e => e.DevicePath, StringComparer.Ordinal)],
            Folders = [.. Folders.Order(StringComparer.Ordinal)],
        };
    }
}
