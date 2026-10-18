using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PlexPod.Core.ItunesDb;
using PlexPod.Core.Stats;
using PlexPod.Core.Media;
using PlexPod.Core.Models;
using PlexPod.Device;
using PlexPod.Core.Sync;
using PlexPod.Core.Tagging;
using PlexPod.Device.Sync;
using PlexPod.Media.Art;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Stats;

namespace PlexPod.Media.Sync;

/// <summary>
/// Makes the iPod's PlexPod content match the selection (M3 plan §5), in the mandated order:
/// plays and ratings to Plex (M4, step 0) → [make room] → moves → tracks (produce, copy, read back,
/// rename) → playlists → line check (P6) → removals last → commit. Only files in the manifest are
/// ever changed or deleted.
/// </summary>
public sealed class SyncEngine(
    TrackPipeline pipeline,
    AlbumArtProvider art,
    MediaPaths paths,
    DeviceFileOps files,
    StatsService stats,
    ILogger<SyncEngine> logger)
{
    /// <summary>Allowance per track for tags and embedded art written on top of the audio.</summary>
    public const long TagAllowanceBytes = 256 * 1024;

    public const string CoverFileName = "cover.jpg";
    public const string SelectionFileName = "selection.json";
    public const string ReportFileName = "last-sync.txt";
    public const string NotOursFileName = "not-ours.json";

    private const int ProducerConcurrency = 2;

    /// <summary>Free space on the device; replaceable in tests to exercise the fit gate and make-room mode.</summary>
    internal Func<string, long> FreeSpace { get; init; } = DeviceFileOps.FreeBytes;

    /// <summary>Identifies the volume, so a different iPod plugged in between plan and sync is caught.</summary>
    internal Func<string, string> Fingerprint { get; init; } = DefaultFingerprint;
    private const int SaveEveryFiles = 20;
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // ---------------------------------------------------------------- preview (read-only)

    public async Task<SyncPreview> PreviewAsync(SyncRequest request, CancellationToken ct, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        progress?.Report("Reading PlexPod's records on the iPod…");
        var root = request.DriveRoot;
        var store = new DeviceStore(root);
        var identity = await store.TryReadIdentityAsync(ct).ConfigureAwait(false);
        var (loaded, source) = await store.LoadManifestAsync(ct).ConfigureAwait(false);
        var notes = new List<string>();
        var manifest = Sanitize(loaded ?? DeviceManifest.Empty(identity?.DeviceId ?? string.Empty), notes);

        progress?.Report("Listing files on the iPod…");
        var stock = request.Firmware == IpodFirmware.Stock;
        string[] folders = stock ? ["iPod_Control/Music", "iPod_Control/Artwork", "Music", "Playlists"] : ["Music", "Playlists"];
        var listing = await Task.Run(() => DeviceFileOps.ListFiles(root, folders), ct).ConfigureAwait(false);
        manifest = Reconcile(manifest, listing, notes);
        var owned = OwnedPaths(manifest);
        var unowned = listing.Where(f => !owned.Contains(f)).ToList();

        // Files checked on an earlier plan and unchanged since (same size and time) aren't re-read.
        var known = await LoadNotOursAsync(store, ct).ConfigureAwait(false);
        var stillNotOurs = new List<ScannedFile>();
        bool KnownNotOurs(string path)
        {
            if (known.TryGetValue(path, out var previous) && ScannedFile.Stat(root, path) is { } now
                && now.SizeBytes == previous.SizeBytes && now.ModifiedTicks == previous.ModifiedTicks)
            {
                stillNotOurs.Add(now);
                return true;
            }

            return false;
        }

        var scanned = new Progress<int>(n => progress?.Report($"Checking files PlexPod doesn't know yet: {n:N0} of {unowned.Count:N0}…"));
        var adoption = await Task.Run(() => Adopter.Scan(root, unowned, ct, scanned, KnownNotOurs), ct).ConfigureAwait(false);
        progress?.Report("Comparing with Plex…");

        var blockers = new List<string>();
        if (stock && request.SignedDatabase is { } signed)
        {
            // PlexPod's unsigned database would be ignored and the iPod would show an empty library.
            blockers.Add($"{signed.Message} ({signed.Evidence})");
        }

        var unplayable = new List<string>();
        var targets = BuildTargets(request, unplayable);
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

        // Tracks written for the other firmware (a Rockbox-era /Music library on a stock iPod, D13, or the
        // reverse) are never moved or re-tagged into this one: they are removed, last, like any unselected track.
        var otherFirmware = manifest.Tracks.Where(t => IpodPath.IsMusicFile(t.DevicePath) != stock).ToList();
        if (otherFirmware.Count > 0)
        {
            notes.Add($"{otherFirmware.Count} track(s) PlexPod wrote for {(stock ? "Rockbox" : "the Apple firmware")} will be removed; this iPod now runs {(stock ? "Apple's firmware" : "Rockbox")}.");
        }

        var forPlanning = manifest with { Tracks = [.. manifest.Tracks.Except(otherFirmware)] };

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

        // On the stock firmware playlists live in the database, not in files: any playlist files PlexPod
        // wrote before (Rockbox-era) are removed (D13).
        var plan = SyncPlanner.Plan(forPlanning, targets, stock ? [] : playlists, userFiles, t => ArtistOf(request.Snapshot, t));
        if (otherFirmware.Count > 0)
        {
            plan = plan with { TrackRemovals = [.. plan.TrackRemovals, .. otherFirmware] };
        }

        var fit = FitCheck.Evaluate(plan, FreeSpace(root), stock ? StockLibrary.DatabaseReserve(targets.Count) : 0, StillReferenced(root, plan));
        if (fit.Outcome == FitOutcome.TooBig)
        {
            blockers.Add($"The selection doesn't fit: {Mb(fit.OverBy)} MB over, even after removals.");
        }

        StatsPreview? statsPreview = null;
        if (request.Stats is { } statsOptions && !stock)
        {
            progress?.Report("Reading plays on the iPod…");
            statsPreview = await stats.PreviewAsync(root, manifest, statsOptions, PlexHasRating(request.Snapshot), ct).ConfigureAwait(false);
        }

        if (unplayable.Count > 0)
        {
            notes.Add($"{unplayable.Count} selected track(s) are in formats Apple's firmware can't play and were left out ({string.Join(", ", unplayable.Take(3))}).");
        }

        var preview = new SyncPreview(request, identity, manifest, source, adoption, plan, fit, blockers)
        {
            Notes = notes,
            DeviceFingerprint = Fingerprint(root),
            NotOurs = [.. stillNotOurs, .. adoption.NotOurs],
            Stats = statsPreview,
            UnplayableOnStock = unplayable,
        };

        if (!stock)
        {
            return preview;
        }

        progress?.Report("Reading the iPod's library…");
        var signature = StockLibrary.Signature(targets, playlists, MasterName(request));
        var pending = request.Stats is null ? StatsState.Empty : await store.LoadStatsAsync(ct).ConfigureAwait(false);
        var stockStats = await Task.Run(() => CountStockPlays(root, manifest, pending, request.Stats is not null, notes), ct).ConfigureAwait(false);
        return preview with
        {
            StockPlaylists = playlists,
            StockSignature = signature,
            StockStats = (stockStats.Plays + pending.PendingPlays.Values.Sum(), stockStats.Ratings + pending.PendingRatings.Count),
            StockDatabaseNeedsWrite = !plan.IsEmpty
                || manifest.StockDatabaseSignature != signature
                || !File.Exists(DevicePaths.ToLocal(root, DevicePaths.ItunesDbPath))
                || File.Exists(DevicePaths.ToLocal(root, DevicePaths.PlayCountsPath)),
        };
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
            if (preview.IsStock)
            {
                await StockStatsAsync(run).ConfigureAwait(false);
            }
            else
            {
                await StatsAsync(run).ConfigureAwait(false);
            }

            if (preview.UsesMakeRoom)
            {
                if (preview.IsStock)
                {
                    // The firmware must never list a file that's gone: first a database without the tracks
                    // about to be removed (playlists briefly absent, D5), then the removals.
                    await WriteStockDatabaseAsync(run, interim: true).ConfigureAwait(false);
                }

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

            if (preview.IsStock)
            {
                if (preview.StockDatabaseNeedsWrite || run.Added + run.Replaced + run.Retagged + run.Moved > 0)
                {
                    await WriteStockDatabaseAsync(run, interim: false).ConfigureAwait(false);
                }
            }
            else
            {
                await CoversAsync(run).ConfigureAwait(false);
                await PlaylistsAsync(run).ConfigureAwait(false);
            }

            if (!(preview.IsStock ? VerifyStockDatabase(run) : VerifyPlaylists(run)))
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
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            run.Stopped = true;
            run.Warnings.Add("Stopped. The iPod is consistent; sync again to finish.");
            return await SalvageAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && run.Started)
        {
            logger.LogError(ex, "Sync failed");
            run.Failures.Add($"The sync stopped because of an error: {ex.Message} Everything finished so far is recorded; sync again to continue.");
            return await SalvageAsync(run).ConfigureAwait(false);
        }
    }

    /// <summary>After a stop or an error: record everything already done on the device, then report.</summary>
    private async Task<SyncResult> SalvageAsync(Run run)
    {
        try
        {
            return await CommitAsync(run, completed: false).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Couldn't save the manifest after a stopped sync");
            run.Failures.Add("Couldn't save PlexPod's records on the iPod; the next sync will repair them from the files' own tags.");
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

        if (run.Preview.DeviceFingerprint.Length > 0 && Fingerprint(request.DriveRoot) != run.Preview.DeviceFingerprint)
        {
            throw new InvalidOperationException("A different iPod (or drive) is connected than the one that was planned for. Plan the sync again.");
        }

        run.State.DeviceId = identity.DeviceId;
        run.State.Firmware = run.Preview.IsStock ? DeviceManifest.FirmwareStock : DeviceManifest.FirmwareRockbox;
        run.Warnings.AddRange(run.Preview.Notes);
        run.Started = true;
        if (run.Store.MigrateLegacyFolder())
        {
            run.Warnings.Add($"Renamed /{DeviceStore.LegacyFolderName} to /{DeviceStore.FolderName} (the app's new name). Playlists get the new header as they're rewritten.");
        }

        var cleaned = DeviceFileOps.CleanTempFiles(request.DriveRoot);
        if (cleaned > 0)
        {
            run.Warnings.Add($"Removed {cleaned} unfinished temporary file(s) from an interrupted sync.");
        }

        if (run.Preview.Adoption.Tracks.Count + run.Preview.Adoption.Playlists.Count > 0)
        {
            run.Warnings.Add($"Adopted {run.Preview.Adoption.Tracks.Count} track(s) and {run.Preview.Adoption.Playlists.Count} playlist(s) PlexPod had written before.");
        }

        if (run.Preview.ManifestSource == ManifestSource.Previous)
        {
            run.Warnings.Add("The manifest was damaged; recovered from the previous copy.");
        }

        await run.SaveAsync().ConfigureAwait(false); // persists adoption and the device id before any change
    }

    /// <summary>
    /// Step 0 (M4, D11): plays and ratings go to Plex before anything on the iPod changes, so plays of
    /// tracks about to be removed still count. Re-read now rather than taken from the plan, so plays
    /// sent since (Send plays now) aren't sent twice. Plex being down never stops the sync.
    /// </summary>
    private async Task StatsAsync(Run run)
    {
        var request = run.Preview.Request;
        if (request.Stats is not { } options)
        {
            return;
        }

        run.Report("Sending plays to Plex", 0, 0, string.Empty);
        var fresh = await stats.PreviewAsync(request.DriveRoot, run.Preview.Manifest, options, PlexHasRating(request.Snapshot), run.Stop).ConfigureAwait(false);
        var sending = new Progress<(int Done, int Total)>(p => run.Report("Sending plays to Plex", p.Done, p.Total, string.Empty));
        run.Stats = await stats.SendAsync(fresh, request.DriveRoot, run.State.DeviceId, request.Client, run.Stop, sending).ConfigureAwait(false);
    }

    /// <summary>D10 "Only fill gaps": whether Plex already has a rating for a track (from the loaded track list).</summary>
    public static Func<string, bool> PlexHasRating(LibrarySnapshot snapshot) =>
        key => snapshot.FindTrack(key)?.UserRating is not null;

    // ---------------------------------------------------------------- stock firmware (M6)

    private static string MasterName(SyncRequest request) =>
        string.IsNullOrWhiteSpace(request.VolumeLabel) ? "iPod" : request.VolumeLabel.Trim();

    /// <summary>Plex's 0–10 rating as the firmware's stars × 20.</summary>
    private static byte PlexRating(TrackItem track) =>
        track.UserRating is { } r ? (byte)Math.Clamp(Math.Round(r) * 10, 0, 100) : (byte)0;

    private static ItdbReadResult? TryReadDatabase(string root, List<string>? warnings = null)
    {
        var local = DevicePaths.ToLocal(root, DevicePaths.ItunesDbPath);
        try
        {
            return File.Exists(local) ? ItunesDbReader.Read(File.ReadAllBytes(local)) : null;
        }
        catch (Exception ex) when (ex is ItunesDbFormatException or IOException or UnauthorizedAccessException)
        {
            warnings?.Add($"The iPod's existing iTunesDB couldn't be read ({ex.Message}); it will be replaced.");
            return null;
        }
    }

    /// <summary>The iPod's Play Counts as read for one sync.</summary>
    /// <param name="Bytes">The file, or null when there is none.</param>
    /// <param name="Usable">False when it couldn't be parsed or there's no database to line it up with: then it's set aside, not consumed.</param>
    private sealed record PlayCountsRead(byte[]? Bytes, string? Sha, bool Usable, List<(ManifestTrack Owned, ItdbTrack OnIpod, PlayCountEntry Entry)> Matches);

    /// <summary>
    /// Lines the firmware's Play Counts up with the database it was recorded against: entry i belongs
    /// to the database's track i. Only tracks PlexPod owns (by database id) are matched.
    /// </summary>
    private static PlayCountsRead ReadPlayCounts(string root, IEnumerable<ManifestTrack> owned, List<string> warnings)
    {
        var matches = new List<(ManifestTrack, ItdbTrack, PlayCountEntry)>();
        var local = DevicePaths.ToLocal(root, DevicePaths.PlayCountsPath);
        if (!File.Exists(local))
        {
            return new PlayCountsRead(null, null, false, matches);
        }

        var bytes = File.ReadAllBytes(local);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        IReadOnlyList<PlayCountEntry> entries;
        try
        {
            entries = PlayCountsReader.Read(bytes);
        }
        catch (ItunesDbFormatException ex)
        {
            warnings.Add($"The iPod's Play Counts file couldn't be read ({ex.Message}); it was kept as \"{Path.GetFileName(DevicePaths.PlayCountsBackupPath)}\".");
            return new PlayCountsRead(bytes, sha, false, matches);
        }

        var db = TryReadDatabase(root, warnings);
        if (db is null)
        {
            warnings.Add($"The iPod has plays recorded but no database to match them to; the file was kept as \"{Path.GetFileName(DevicePaths.PlayCountsBackupPath)}\".");
            return new PlayCountsRead(bytes, sha, false, matches);
        }

        var byDbid = owned.Where(t => t.Stock is not null).GroupBy(t => t.Stock!.Dbid).ToDictionary(g => g.Key, g => g.First());
        for (var i = 0; i < Math.Min(entries.Count, db.Tracks.Count); i++)
        {
            var onIpod = db.Tracks[i].Track;
            var entry = entries[i];
            var ratingChanged = entry.Rating is { } r && r != onIpod.Rating;
            if ((entry.Plays > 0 || entry.Skips > 0 || ratingChanged) && byDbid.TryGetValue(onIpod.Dbid, out var mine))
            {
                matches.Add((mine, onIpod, entry));
            }
        }

        return new PlayCountsRead(bytes, sha, true, matches);
    }

    /// <summary>For the plan dialog: plays and rating changes in Play Counts not yet taken (read-only).</summary>
    private static (int Plays, int Ratings) CountStockPlays(string root, DeviceManifest manifest, StatsState state, bool statsOn, List<string> notes)
    {
        if (!statsOn)
        {
            return (0, 0);
        }

        var read = ReadPlayCounts(root, manifest.Tracks, notes);
        if (!read.Usable || read.Sha == state.ConsumedPlayCounts)
        {
            return (0, 0);
        }

        return ((int)read.Matches.Sum(m => m.Entry.Plays), read.Matches.Count(m => m.Entry.Rating is { } r && r != m.OnIpod.Rating));
    }

    /// <summary>
    /// Step 0 on the stock firmware (M6 plan §4.3): fold Play Counts into the manifest (so every rebuilt
    /// database keeps the iPod's counts and ratings), keep the plays and rating changes for Plex as
    /// pending, delete Play Counts as iTunes does (D14), then send everything pending to Plex. Plex
    /// being down never stops the sync; what isn't accepted stays pending for next time.
    /// </summary>
    private async Task StockStatsAsync(Run run)
    {
        var request = run.Preview.Request;
        run.Report("Reading plays on the iPod", 0, 0, string.Empty);
        var state = await run.Store.LoadStatsAsync(CancellationToken.None).ConfigureAwait(false);
        var pendingPlays = new Dictionary<string, int>(state.PendingPlays, StringComparer.Ordinal);
        var pendingRatings = new Dictionary<string, int>(state.PendingRatings, StringComparer.Ordinal);
        var warnings = new List<string>();
        var read = ReadPlayCounts(run.Root, run.State.Tracks.Values.ToList(), warnings);
        run.Warnings.AddRange(warnings);
        if (read.Bytes is not null && !read.Usable)
        {
            // ML1: never destroy plays PlexPod couldn't read; and never leave them for the next
            // rebuilt database to be lined up against the wrong tracks.
            SetPlayCountsAside(run);
        }
        else if (read.Bytes is not null)
        {
            // M1: the file's fingerprint, kept with each half, is the bookmark. Counts go into the manifest
            // only if these exact bytes haven't been folded in before, and plays into pending likewise.
            var foldCounts = run.State.ConsumedPlayCounts != read.Sha;
            var foldPending = request.Stats is not null && state.ConsumedPlayCounts != read.Sha;
            foreach (var (owned, onIpod, entry) in read.Matches)
            {
                var current = run.State.Tracks.GetValueOrDefault(owned.DevicePath) ?? owned;
                if (foldCounts)
                {
                    var record = current.Stock!;
                    run.State.Tracks[current.DevicePath] = current with
                    {
                        Stock = record with
                        {
                            PlayCount = record.PlayCount + entry.Plays,
                            LastPlayed = Later(record.LastPlayed, entry.LastPlayed),
                            SkipCount = record.SkipCount + entry.Skips,
                            LastSkipped = Later(record.LastSkipped, entry.LastSkipped),
                            Rating = entry.Rating ?? record.Rating,
                        },
                    };
                }

                if (foldPending && entry.Plays > 0)
                {
                    pendingPlays[current.RatingKey] = pendingPlays.GetValueOrDefault(current.RatingKey) + (int)Math.Min(entry.Plays, 1000);
                }

                if (foldPending && entry.Rating is { } stars && stars != onIpod.Rating)
                {
                    pendingRatings[current.RatingKey] = stars == 0 ? -1 : stars / 10;
                }
            }

            run.State.ConsumedPlayCounts = read.Sha;
            state = state with { PendingPlays = pendingPlays, PendingRatings = pendingRatings, ConsumedPlayCounts = read.Sha };
            await run.Store.SaveStatsAsync(state, CancellationToken.None).ConfigureAwait(false);
            await run.SaveAsync().ConfigureAwait(false);
            try
            {
                DeviceFileOps.Delete(run.Root, DevicePaths.PlayCountsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Directory.Exists(run.Root))
            {
                // Harmless now: the fingerprint stops these bytes being counted again.
                run.Warnings.Add($"The iPod's Play Counts file couldn't be deleted ({ex.Message}); its plays are recorded and won't be counted twice.");
            }
        }

        if (request.Stats is not { } options || pendingPlays.Count + pendingRatings.Count == 0)
        {
            return;
        }

        run.Report("Sending plays to Plex", 0, 0, string.Empty);
        int plays = 0, ratings = 0;
        string? error = null;
        var hasRating = PlexHasRating(request.Snapshot);
        try
        {
            foreach (var key in pendingPlays.Keys.ToList())
            {
                while (pendingPlays.TryGetValue(key, out var left) && left > 0)
                {
                    run.Stop.ThrowIfCancellationRequested();
                    await request.Client.ScrobbleAsync(key, run.Stop).ConfigureAwait(false);
                    plays++;
                    if (left == 1)
                    {
                        pendingPlays.Remove(key);
                    }
                    else
                    {
                        pendingPlays[key] = left - 1;
                    }
                }
            }

            foreach (var (key, value) in pendingRatings.ToList())
            {
                run.Stop.ThrowIfCancellationRequested();
                if (!(options.Ratings == RatingMode.FillGapsOnly && hasRating(key)))
                {
                    await request.Client.RateAsync(key, value, run.Stop).ConfigureAwait(false);
                    ratings++;
                }

                pendingRatings.Remove(key);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or Plex.Http.PlexProtocolException
                                   || (ex is OperationCanceledException && !run.Stop.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Plex stopped accepting plays after {Plays} play(s)", plays);
            error = "Couldn't reach Plex.";
        }
        finally
        {
            await run.Store.SaveStatsAsync(state with { PendingPlays = pendingPlays, PendingRatings = pendingRatings }, CancellationToken.None).ConfigureAwait(false);
        }

        var waitingPlays = pendingPlays.Values.Sum();
        var lines = new List<string>();
        if (plays + ratings > 0)
        {
            lines.Add($"Sent {StatsService.Describe(plays, ratings)} from the iPod to Plex.");
        }

        if (error is not null)
        {
            lines.Add($"{error} {StatsService.Describe(waitingPlays, pendingRatings.Count)} will be sent next time.");
        }

        run.Stats = new StatsResult(plays, ratings, waitingPlays, pendingRatings.Count, 0, 0, error, lines);
    }

    /// <summary>Moves an unusable Play Counts aside (replacing an older one), so it's kept but never matched against a rebuilt database.</summary>
    private void SetPlayCountsAside(Run run)
    {
        try
        {
            DeviceFileOps.Delete(run.Root, DevicePaths.PlayCountsBackupPath);
            files.Move(run.Root, DevicePaths.PlayCountsPath, DevicePaths.PlayCountsBackupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Directory.Exists(run.Root))
        {
            run.Warnings.Add($"The iPod's unreadable Play Counts file couldn't be set aside ({ex.Message}).");
        }
    }

    private static DateTime? Later(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Builds the whole iTunesDB from the manifest, checks it parses back, and swaps it in through
    /// DeviceFileOps (temp file, read-back, rename): the firmware never sees a half-written database.
    /// The tracks this sync removes are left out, because they are deleted after the swap. The interim
    /// database (make-room mode, before the removals) has no playlists: they are briefly absent (D5).
    /// </summary>
    private async Task WriteStockDatabaseAsync(Run run, bool interim)
    {
        run.Report("Writing the iPod's library", 0, 1, string.Empty);
        var failures = new List<string>();
        var warnings = new List<string>();
        var previous = TryReadDatabase(run.Root, warnings);
        var removing = run.Preview.Plan.TrackRemovals.Select(t => t.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var library = StockLibrary.Build(
            run.State.Tracks.Values.Where(t => !removing.Contains(t.DevicePath)),
            interim ? [] : run.Preview.StockPlaylists,
            run.State.DeviceId,
            MasterName(run.Preview.Request),
            t => StockLibrary.FromFile(DevicePaths.ToLocal(run.Root, t.DevicePath), t),
            previous,
            failures,
            warnings);
        StockArtwork? artwork = null;
        if (!interim)
        {
            artwork = await WriteStockArtworkAsync(run, library).ConfigureAwait(false);
            library = artwork?.Library ?? library;
        }

        var bytes = ItunesDbWriter.Write(library);
        ItunesDbReader.Read(bytes); // never put a database on the iPod that doesn't read back

        var temp = Path.Combine(paths.CacheRoot, "sync-staging", Guid.NewGuid().ToString("N") + ".itdb");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
        try
        {
            await files.CopyVerifiedAsync(temp, run.Root, DevicePaths.ItunesDbPath, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
        }

        // Thumbnail files the new ArtworkDB no longer uses (the previous index) go only now.
        foreach (var stale in run.State.Extras.Values.Where(e => e.Kind == "artwork" && artwork is not null && !artwork.InUse.Contains(e.DevicePath)).ToList())
        {
            DeviceFileOps.Delete(run.Root, stale.DevicePath);
            run.State.Extras.Remove(stale.DevicePath);
        }

        run.Warnings.AddRange(warnings);
        if (!interim)
        {
            run.Failures.AddRange(failures);
            run.StockLibrary = (library.Tracks.Count, library.Playlists.Count);

            // Remembered only when complete, so a partial database is rebuilt next time.
            run.State.StockSignature = run.Failures.Count == 0 ? run.Preview.StockSignature : null;
        }

        await run.SaveAsync().ConfigureAwait(false);
    }

    private const string ArtworkFolder = "/iPod_Control/Artwork/";

    /// <summary>The library with cover links set, and the artwork files the new ArtworkDB uses.</summary>
    private sealed record StockArtwork(ItdbLibrary Library, IReadOnlySet<string> InUse);

    /// <summary>
    /// D15: covers for the stock firmware (M6 plan §4.4). Thumbnails are made per album from the cached
    /// cover, written per track in database order (this iPod has no shared artwork), into the other
    /// file index whenever they change, so the ArtworkDB on the iPod never points into a half-written
    /// file. Art never fails a sync: on any problem the library goes on without covers.
    /// </summary>
    private async Task<StockArtwork?> WriteStockArtworkAsync(Run run, ItdbLibrary library)
    {
        run.Report("Writing album art", 0, library.Tracks.Count, string.Empty);
        var staging = Directory.CreateDirectory(Path.Combine(paths.CacheRoot, "sync-staging")).FullName;
        var formats = ArtworkFormat.IpodVideo;
        var temps = formats.ToDictionary(f => f.FormatId, f => Path.Combine(staging, Guid.NewGuid().ToString("N") + ".ithmb"));
        try
        {
            var keyByDbid = run.State.Tracks.Values.Where(t => t.Stock is not null)
                .GroupBy(t => t.Stock!.Dbid).ToDictionary(g => g.Key, g => g.First().RatingKey);
            var thumbsByAlbum = new Dictionary<string, (byte[] Source, (byte[] Pixels, int Width, int Height, int HorizontalPadding, int VerticalPadding)[] Thumbs)?>(StringComparer.Ordinal);
            var placed = new List<(int Track, uint SourceSize, int Offset)>();
            var streams = formats.ToDictionary(f => f.FormatId, f => new FileStream(temps[f.FormatId], FileMode.Create, FileAccess.Write));
            var perAlbumThumbs = new Dictionary<int, (byte[] Pixels, int Width, int Height, int HorizontalPadding, int VerticalPadding)[]>();
            try
            {
                for (var i = 0; i < library.Tracks.Count; i++)
                {
                    var albumKey = keyByDbid.TryGetValue(library.Tracks[i].Dbid, out var ratingKey)
                        ? (run.Preview.Request.Snapshot.FindTrack(ratingKey) ?? PlaylistTrack(run, ratingKey))?.AlbumKey
                        : null;
                    if (albumKey is null)
                    {
                        continue;
                    }

                    if (!thumbsByAlbum.TryGetValue(albumKey, out var art))
                    {
                        art = null;
                        var images = await run.ArtAsync(run.AlbumFromSnapshot(albumKey)).ConfigureAwait(false);
                        if (images is not null)
                        {
                            try
                            {
                                art = (images.CoverJpeg, formats.Select(f => ArtProcessor.ToRgb565(images.CoverJpeg, f.Width, f.Height)).ToArray());
                            }
                            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
                            {
                                logger.LogWarning("Cover for album {Album} couldn't be made into iPod thumbnails: {Message}", albumKey, ex.Message);
                            }
                        }

                        thumbsByAlbum[albumKey] = art;
                    }

                    if (art is not { } cover)
                    {
                        continue;
                    }

                    // This iPod reads a separate copy per track (no shared artwork), at the same offset in every size's file.
                    var offset = placed.Count;
                    for (var f = 0; f < formats.Count; f++)
                    {
                        await streams[formats[f].FormatId].WriteAsync(cover.Thumbs[f].Pixels).ConfigureAwait(false);
                    }

                    placed.Add((i, (uint)cover.Source.Length, offset));
                    perAlbumThumbs[i] = cover.Thumbs;
                }
            }
            finally
            {
                foreach (var stream in streams.Values)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }

            // Reuse a format's current file when its bytes are unchanged; otherwise write the other index.
            var chosen = new Dictionary<int, (string DevicePath, bool Write, string Sha)>();
            foreach (var format in formats)
            {
                string sha;
                var pixels = File.OpenRead(temps[format.FormatId]);
                await using (pixels.ConfigureAwait(false))
                {
                    sha = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(pixels).ConfigureAwait(false));
                }

                var current = run.State.Extras.Values.FirstOrDefault(e => e.Kind == "artwork"
                    && (e.DevicePath == ArtworkFolder + format.FileName(1) || e.DevicePath == ArtworkFolder + format.FileName(2)));
                if (current is not null && current.Sha256 == sha && File.Exists(DevicePaths.ToLocal(run.Root, current.DevicePath)))
                {
                    chosen[format.FormatId] = (current.DevicePath, false, sha);
                }
                else
                {
                    var index = current?.DevicePath == ArtworkFolder + format.FileName(1) ? 2 : 1;
                    chosen[format.FormatId] = (ArtworkFolder + format.FileName(index), true, sha);
                }
            }

            var entries = new List<ArtworkEntry>();
            var tracks = library.Tracks.ToArray();
            foreach (var (track, sourceSize, offset) in placed)
            {
                var id = ArtworkDb.FirstImageId + (uint)entries.Count;
                var thumbs = formats.Select((f, n) =>
                {
                    var t = perAlbumThumbs[track][n];
                    return new ArtworkThumb(f.FormatId, Path.GetFileName(chosen[f.FormatId].DevicePath), (uint)(offset * f.ImageBytes), (uint)f.ImageBytes,
                        (ushort)t.Width, (ushort)t.Height, (ushort)t.HorizontalPadding, (ushort)t.VerticalPadding);
                }).ToList();
                entries.Add(new ArtworkEntry(id, tracks[track].Dbid, sourceSize, thumbs));
                tracks[track] = tracks[track] with { ArtworkId = id, ArtworkSize = sourceSize };
            }

            var artworkDb = ArtworkDb.Write(entries, formats);
            ArtworkDb.Read(artworkDb); // reads back, or nothing goes on the iPod

            foreach (var format in formats)
            {
                var (devicePath, write, sha) = chosen[format.FormatId];
                if (write)
                {
                    await files.CopyVerifiedAsync(temps[format.FormatId], run.Root, devicePath, CancellationToken.None).ConfigureAwait(false);
                    run.State.Extras[devicePath] = new ManifestExtra(devicePath, "artwork", sha, ArtworkFolder.TrimEnd('/'));
                }
            }

            var dbPath = ArtworkFolder + "ArtworkDB";
            var dbSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(artworkDb));
            if (run.State.Extras.GetValueOrDefault(dbPath)?.Sha256 != dbSha || !File.Exists(DevicePaths.ToLocal(run.Root, dbPath)))
            {
                var temp = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".artdb");
                await File.WriteAllBytesAsync(temp, artworkDb).ConfigureAwait(false);
                try
                {
                    await files.CopyVerifiedAsync(temp, run.Root, dbPath, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(temp);
                }

                run.State.Extras[dbPath] = new ManifestExtra(dbPath, "artwork", dbSha, ArtworkFolder.TrimEnd('/'));
            }

            await run.SaveAsync().ConfigureAwait(false);
            var inUse = chosen.Values.Select(c => c.DevicePath).Append(dbPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new StockArtwork(library with { Tracks = tracks }, inUse);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ItunesDbFormatException or DeviceVerifyException
                                   && Directory.Exists(run.Root))
        {
            logger.LogWarning(ex, "Album art for the iPod's library failed");
            run.Warnings.Add($"Album art couldn't be written ({ex.Message}); the library is written without covers this time.");
            return null;
        }
        finally
        {
            foreach (var temp in temps.Values)
            {
                TryDelete(temp);
            }
        }
    }

    private static TrackItem? PlaylistTrack(Run run, string ratingKey) =>
        run.Preview.Request.PlaylistContents.Values.SelectMany(c => c.Entries).Select(e => e.Track).FirstOrDefault(t => t?.RatingKey == ratingKey);

    /// <summary>P6 for the stock firmware: the database on the iPod reads back, and every file it names is there.</summary>
    private bool VerifyStockDatabase(Run run)
    {
        run.Report("Checking the iPod's library", 0, 1, string.Empty);
        var problems = new List<string>();
        var local = DevicePaths.ToLocal(run.Root, DevicePaths.ItunesDbPath);
        if (!File.Exists(local))
        {
            return run.State.Tracks.Values.All(t => !IpodPath.IsMusicFile(t.DevicePath));
        }

        try
        {
            var db = ItunesDbReader.Read(File.ReadAllBytes(local));
            foreach (var track in db.Tracks)
            {
                var devicePath = IpodPath.FromColon(track.Track.IpodPath);
                if (!DevicePaths.IsWellFormed(devicePath) || !File.Exists(DevicePaths.ToLocal(run.Root, devicePath)))
                {
                    problems.Add($"{track.Track.Title}: {devicePath} is missing");
                }
            }
        }
        catch (Exception ex) when (ex is ItunesDbFormatException or IOException or ArgumentException)
        {
            problems.Add($"The iTunesDB doesn't read back: {ex.Message}");
        }

        if (problems.Count == 0)
        {
            return true;
        }

        run.Failures.Add($"Library check failed ({problems.Count} problem(s)); nothing was removed:");
        run.Failures.AddRange(problems.Take(20).Select(p => "  " + p));
        return false;
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

        if (moves.Count > 0)
        {
            await run.SaveAsync().ConfigureAwait(false);
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
            AlbumDetail album;
            ArtistDetail? artist;
            string? mbid;
            try
            {
                album = await run.AlbumAsync(op.Target.Track.AlbumKey).ConfigureAwait(false);
                artist = await run.ArtistForGenreAsync(album).ConfigureAwait(false);
                mbid = request.Policy.MusicBrainzTrackId ? await run.MbidAsync(op.Target.Track.RatingKey).ConfigureAwait(false) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Fail(op, $"couldn't read its details from Plex ({ex.Message})");
                continue;
            }

            var expectedTags = TagMapper.Map(
                new TagSource(op.Target.Track, album, artist, request.Snapshot.TracksByAlbum(op.Target.Track.AlbumKey).ToList(), mbid, new SourceTags(existing.SourceComposer, existing.SourceComment)),
                request.Policy);

            // A file written before the rebrand is current if only its ID tags' names differ; it gets the
            // new names the next time its tags really change.
            if (expectedTags.ContentHash() == existing.TagHash || expectedTags.LegacyContentHash() == existing.TagHash)
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
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Producing happens on the PC (download, convert, tag), so one track's problem never stops the rest.
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
                catch (Exception ex) when (ex is DeviceVerifyException or ArgumentException or InvalidOperationException
                    || (ex is IOException or UnauthorizedAccessException && Directory.Exists(run.Root)))
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
        var output = Path.Combine(staging, Guid.NewGuid().ToString("N") + Path.GetExtension(op.DevicePath));
        var job = new TrackJob(track, album, artist, request.Snapshot.TracksByAlbum(track.AlbumKey).ToList(), mbid, images, request.Policy, output,
            Target: request.Firmware);
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
            Stock = run.Preview.IsStock
                ? StockLibrary.Record(processed, op.DevicePath, op.Target.Track.RatingKey, op.Existing?.Stock, PlexRating(op.Target.Track))
                : null,
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

    /// <summary>A <c>cover.jpg</c> in every album folder PlexPod fills (PictureFlow), never replacing the user's own.</summary>
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

            // The file PlexPod wrote has been replaced (its hash no longer matches the manifest): the
            // user put their own art there. Hand it over to them rather than overwrite it.
            if (extra is not null && userFiles.Contains(coverPath) && DeviceFileOps.HashFromDevice(DevicePaths.ToLocal(run.Root, coverPath)) != extra.Sha256)
            {
                run.State.Extras.Remove(coverPath);
                run.Warnings.Add($"Kept your own {coverPath}; PlexPod won't change it again.");
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

    /// <summary>
    /// P6 for the playlists this sync keeps. Stale ones about to be removed aren't checked: their lines
    /// may already point at files deleted by hand or moved, and failing on them would block their own
    /// removal on every sync from then on.
    /// </summary>
    private bool VerifyPlaylists(Run run)
    {
        run.Report("Checking playlists", 0, 1, string.Empty);
        var removing = run.Preview.Plan.PlaylistRemovals.Select(p => p.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new PlaylistVerifier().Verify(run.Root, run.State.Playlists.Keys.Where(p => !removing.Contains(p)));
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

        // Belt and braces for P5: never delete a file any PlexPod playlist on the iPod still names.
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
        foreach (var cover in run.State.Extras.Values.Where(e => e.Kind == "cover" && !trackFolders.Contains(Parent(e.DevicePath))).ToList())
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
        await run.Store.WriteTextAsync(NotOursFileName, JsonSerializer.Serialize(run.Preview.NotOurs, Json), CancellationToken.None).ConfigureAwait(false);
        var result = run.Finish(completed, logger);
        await run.Store.WriteTextAsync(ReportFileName, result.Report, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// For the fit check: files named by PlexPod playlists the sync leaves as they are (not removed, not
    /// rewritten). The removal step's P5 guard won't delete those, so they mustn't count as freed space.
    /// </summary>
    private static HashSet<string> StillReferenced(string root, SyncPlan plan)
    {
        var changing = plan.PlaylistRemovals.Select(p => p.DevicePath)
            .Concat(plan.PlaylistOps.Where(p => p.NeedsWrite).Select(p => p.DevicePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in plan.PlaylistOps.Where(p => p.Existing is not null && !changing.Contains(p.Existing.DevicePath)))
        {
            var local = DevicePaths.ToLocal(root, op.Existing!.DevicePath);
            if (File.Exists(local))
            {
                referenced.UnionWith(PlaylistWriter.TrackLines(File.ReadAllText(local)).Select(l => l.Path));
            }
        }

        return referenced;
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

    internal static List<SyncTrackTarget> BuildTargets(SyncRequest request, List<string>? unplayable = null)
    {
        var stock = request.Firmware == IpodFirmware.Stock;
        var stockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            string path;
            long estimate;
            if (stock)
            {
                // Apple's own layout: iPod_Control/Music/Fnn/ABCD.ext; FLAC becomes Apple Lossless (D12).
                var source = TrackPipeline.SourceExtension(track.Parts[0]);
                var isFlac = source == ".flac";
                var ext = isFlac ? ".m4a" : Ffmpeg.ContainerCleaner.OutputExtension(source);
                if (!isFlac && ItdbFileKinds.FromExtension(ext) is null)
                {
                    unplayable?.Add(track.Title);
                    continue;
                }

                // Two tracks hashing to the same short name get another name, never a " (2)" suffix.
                var attempt = 0;
                do
                {
                    path = IpodPath.ForTrack(track.RatingKey, ext, attempt++);
                }
                while (!stockNames.Add(path));
                var flac = SizeEstimator.EstimateBytes(track, request.FormatOverrides.GetValueOrDefault(key) ?? track.Format).Bytes;
                estimate = isFlac ? flac + (flac / 10) : flac; // Apple Lossless runs a few % larger than FLAC
            }
            else
            {
                var multiDisc = snapshot.TracksByAlbum(track.AlbumKey).Select(t => t.DiscIndex ?? 1).Distinct().Count() > 1;
                path = DevicePathBuilder.Build(
                    new PathFields(album?.ArtistTitle ?? ArtistOf(snapshot, track), album?.Year, album?.Title, track.DiscIndex, track.Index, track.Title),
                    TrackPipeline.DeviceExtension(track.Parts[0]),
                    multiDisc);
                estimate = request.KnownSizes.TryGetValue(key, out var known)
                    ? known
                    : SizeEstimator.EstimateBytes(track, request.FormatOverrides.GetValueOrDefault(key) ?? track.Format).Bytes;
            }
            targets.Add(new SyncTrackTarget(track, path, estimate + TagAllowanceBytes, SyncTrackTarget.Stamp(track.UpdatedAt, album?.UpdatedAt, fingerprint)));
        }

        return targets;
    }

    internal static string ArtistOf(LibrarySnapshot snapshot, TrackItem track) =>
        track.TrackArtist
        ?? snapshot.FindAlbum(track.AlbumKey)?.ArtistTitle
        ?? snapshot.FindArtist(track.ArtistKey)?.Title
        ?? "Unknown Artist";

    /// <summary>
    /// The manifest is read from the iPod, so it is untrusted: entries outside /Music and /Playlists
    /// (or malformed) are dropped, never acted on.
    /// </summary>
    internal static DeviceManifest Sanitize(DeviceManifest manifest, List<string> notes)
    {
        var tracks = manifest.Tracks.Where(t => DevicePaths.IsManaged(t.DevicePath) && DevicePaths.IsManaged(t.DesiredPath)).ToList();
        var playlists = manifest.Playlists.Where(p => DevicePaths.IsManaged(p.DevicePath)).ToList();
        var extras = manifest.Extras.Where(e => DevicePaths.IsManaged(e.DevicePath)).ToList();
        var folders = manifest.Folders.Where(DevicePaths.IsManaged).ToList();
        var dropped = manifest.Tracks.Count - tracks.Count + manifest.Playlists.Count - playlists.Count + manifest.Extras.Count - extras.Count;
        if (dropped > 0)
        {
            notes.Add($"Ignored {dropped} entr{(dropped == 1 ? "y" : "ies")} in PlexPod's records that pointed outside /Music and /Playlists.");
        }

        return manifest with { Tracks = tracks, Playlists = playlists, Extras = extras, Folders = folders };
    }

    /// <summary>
    /// Drops manifest entries whose files are gone (deleted by hand, or lost to an interrupted sync),
    /// so they are planned again instead of wedging every later sync.
    /// </summary>
    internal static DeviceManifest Reconcile(DeviceManifest manifest, IReadOnlyCollection<string> listing, List<string> notes)
    {
        var present = listing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracks = manifest.Tracks.Where(t => present.Contains(t.DevicePath)).ToList();
        var playlists = manifest.Playlists.Where(p => present.Contains(p.DevicePath)).ToList();
        var extras = manifest.Extras.Where(e => present.Contains(e.DevicePath)).ToList();
        var missing = manifest.Tracks.Count - tracks.Count + manifest.Playlists.Count - playlists.Count;
        if (missing > 0)
        {
            notes.Add($"{missing} file(s) PlexPod had copied were missing from the iPod; they will be copied again if still selected.");
        }

        return manifest with { Tracks = tracks, Playlists = playlists, Extras = extras };
    }

    private static async Task<Dictionary<string, ScannedFile>> LoadNotOursAsync(DeviceStore store, CancellationToken ct)
    {
        try
        {
            var json = await store.TryReadTextAsync(NotOursFileName, ct).ConfigureAwait(false);
            var list = json is null ? null : JsonSerializer.Deserialize<List<ScannedFile>>(json, Json);
            return (list ?? []).Where(f => DevicePaths.IsManaged(f.DevicePath))
                .GroupBy(f => f.DevicePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, ScannedFile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string DefaultFingerprint(string root)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            return FormattableString.Invariant($"{drive.VolumeLabel}|{drive.TotalSize}|{drive.DriveFormat}");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

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

        /// <summary>Written by producer threads, read by the device writer: must be thread-safe.</summary>
        public ConcurrentDictionary<string, string?> Mbids { get; } = new(StringComparer.Ordinal);

        /// <summary>Set once preflight passed; from then on, any failure saves the manifest before reporting.</summary>
        public bool Started { get; set; }

        /// <summary>Stock firmware: tracks and playlists in the database written this sync.</summary>
        public (int Tracks, int Playlists)? StockLibrary { get; set; }

        public StatsResult? Stats { get; set; }

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
            Mbids[ratingKey] = detail?.MusicBrainzTrackId;
            return detail?.MusicBrainzTrackId;
        }

        public SyncResult Finish(bool completed, ILogger logger)
        {
            var report = BuildReport(completed);
            logger.LogInformation("Sync finished: completed={Completed} added={Added} replaced={Replaced} retagged={Retagged} moved={Moved} removed={Removed} playlists={Playlists} failures={Failures}",
                completed, Added, Replaced, Retagged, Moved, Removed, PlaylistsWritten, Failures.Count);
            return new SyncResult(completed, Stopped, Added, Replaced, Retagged, TagsAlreadyCurrent, Moved, Removed, PlaylistsWritten, PlaylistsRemoved,
                BytesWritten, _clock.Elapsed, [.. Failures], [.. Warnings], report)
            {
                Stats = Stats,
            };
        }

        public async Task<ArtImages?> ArtAsync(AlbumDetail album)
        {
            await _lookups.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_art.TryGetValue(album.RatingKey, out var images))
                {
                    images = await _artProvider.GetAsync(Preview.Request.Client, album, Stop, Preview.Request.Ffmpeg).ConfigureAwait(false);
                    _art[album.RatingKey] = images;
                }

                return images;
            }
            finally
            {
                _lookups.Release();
            }
        }

        /// <summary>The album from the library snapshot (no Plex request): enough to find its cached cover.</summary>
        public AlbumDetail AlbumFromSnapshot(string albumKey) => Fallback(albumKey);

        private AlbumDetail Fallback(string albumKey)
        {
            var album = Preview.Request.Snapshot.FindAlbum(albumKey);
            return new AlbumDetail(albumKey, album?.Title ?? string.Empty, album?.ArtistKey ?? string.Empty, album?.ArtistTitle ?? string.Empty,
                album?.Year, null, [], [], null, album?.TrackCount ?? 0, album?.ThumbPath);
        }

        private string BuildReport(bool completed)
        {
            var b = new StringBuilder();
            b.AppendLine(CultureInfo.InvariantCulture, $"PlexPod sync, {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz} ({_clock.Elapsed:hh\\:mm\\:ss})");
            b.AppendLine(completed ? "Result: completed" : Stopped ? "Result: stopped (safe to sync again)" : "Result: finished with problems");
            b.AppendLine(CultureInfo.InvariantCulture, $"Added {Added}, replaced {Replaced}, re-tagged {Retagged} ({TagsAlreadyCurrent} already current), moved {Moved}, removed {Removed}.");
            b.AppendLine(StockLibrary is { } library
                ? string.Create(CultureInfo.InvariantCulture, $"iPod library rebuilt: {library.Tracks} tracks, {library.Playlists} playlists. {BytesWritten / 1048576.0:0.0} MB written, every file read back and verified.")
                : string.Create(CultureInfo.InvariantCulture, $"Playlists written {PlaylistsWritten}, removed {PlaylistsRemoved}. {BytesWritten / 1048576.0:0.0} MB written, every file read back and verified."));
            if (Stats is { Lines.Count: > 0 } sent)
            {
                b.AppendLine().AppendLine("Plays and ratings:");
                foreach (var line in sent.Lines)
                {
                    b.AppendLine("  " + line);
                }
            }

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

            b.AppendLine().AppendLine(Preview.IsStock
                ? "Eject the iPod before unplugging it; the new library shows straight away."
                : "Run Database → Update Now on the iPod (or keep Auto Update on) so the new tracks appear in its menus.");
            return b.ToString();
        }
    }

    /// <summary>The manifest as dictionaries while a sync edits it; snapshotted on every save.</summary>
    private sealed class MutableManifest(DeviceManifest source)
    {
        public string DeviceId { get; set; } = source.DeviceId;

        public string Firmware { get; set; } = source.Firmware;

        public string? StockSignature { get; set; } = source.StockDatabaseSignature;

        public string? ConsumedPlayCounts { get; set; } = source.ConsumedPlayCounts;

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
            Firmware = Firmware,
            StockDatabaseSignature = StockSignature,
            ConsumedPlayCounts = ConsumedPlayCounts,
            Tracks = [.. Tracks.Values.OrderBy(t => t.DevicePath, StringComparer.Ordinal)],
            Playlists = [.. Playlists.Values.OrderBy(p => p.DevicePath, StringComparer.Ordinal)],
            Extras = [.. Extras.Values.OrderBy(e => e.DevicePath, StringComparer.Ordinal)],
            Folders = [.. Folders.Order(StringComparer.Ordinal)],
        };
    }
}
