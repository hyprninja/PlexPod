using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlexPod.Core.Stats;
using PlexPod.Core.Sync;
using PlexPod.Device.Stats;
using PlexPod.Device.Sync;
using PlexPod.Plex.Http;
using PlexPod.Plex.Library;

namespace PlexPod.Media.Stats;

/// <summary>Stats return is on when a request carries these (D11); <see cref="Ratings"/> is D10.</summary>
public sealed record StatsOptions(RatingMode Ratings);

/// <summary>What would be sent, read from the iPod without changing anything.</summary>
public sealed record StatsPreview(StatsPlan Plan, DeviceStatsFiles Files, StatsState State, IReadOnlyList<string> Hints)
{
    public int Plays => Plan.PlaysToSend;

    public int Ratings => Plan.Ratings.Count;

    /// <summary>"12 plays and 3 ratings", or null when there's nothing to send.</summary>
    public string? Summary => Plan.IsEmpty ? null : StatsService.Describe(Plays, Ratings);
}

public sealed record StatsResult(
    int PlaysSent,
    int RatingsSent,
    int PlaysWaiting,
    int RatingsWaiting,
    int TooShort,
    int NotOurs,
    string? Error,
    IReadOnlyList<string> Lines);

/// <summary>
/// Sends plays and ratings from the iPod to Plex (M4 plan §3, §5): in log order, saving the bookmark
/// every few plays, so an outage or unplug resumes where it stopped and a play is never lost.
/// Rockbox's own files are only read (D9).
/// </summary>
public sealed class StatsService(MediaPaths paths, ILogger<StatsService> logger)
{
    internal const int SaveEveryPlays = 10;

    /// <summary>Old bookmarks kept beside the current logs' (most recent first).</summary>
    internal const int MaxOldBookmarks = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The iPod's clock is local time; tests pin the zone.</summary>
    internal TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public async Task<StatsPreview> PreviewAsync(
        string driveRoot,
        DeviceManifest manifest,
        StatsOptions options,
        Func<string, bool> plexHasRating,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(options);
        var files = await Task.Run(() => StatsReader.ReadAsync(driveRoot, ct), ct).ConfigureAwait(false);
        var state = await new DeviceStore(driveRoot).LoadStatsAsync(ct).ConfigureAwait(false);
        var changelog = files.Changelog is null ? [] : ChangelogParser.Parse(files.Changelog);
        var plan = StatsPlanner.Plan(files.Logs, changelog, state, KeysByPath(manifest), options.Ratings, plexHasRating);
        return new StatsPreview(plan, files, state, Hints(files, plan));
    }

    /// <summary>
    /// Sends <paramref name="preview"/>'s plays, then its ratings. Plex failing stops sending and is
    /// reported, not thrown; Stop (<paramref name="ct"/>) saves progress and then throws.
    /// </summary>
    public async Task<StatsResult> SendAsync(
        StatsPreview preview,
        string driveRoot,
        string deviceId,
        IPlexLibraryClient client,
        CancellationToken ct,
        IProgress<(int Done, int Total)>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(client);
        var plan = preview.Plan;
        var store = new DeviceStore(driveRoot);
        var processed = plan.StartOffsets.ToArray();
        var ratingsSent = new Dictionary<string, int>(preview.State.RatingsSent, StringComparer.Ordinal);
        var archive = Archive.TryOpen(paths.StatsArchive, deviceId, logger);
        archive?.KeepLogs(preview.Files.Logs);

        var total = plan.PlaysToSend + plan.Ratings.Count;
        int plays = 0, ratings = 0, sinceSave = 0;
        string? error = null;
        var dirty = false;
        try
        {
            foreach (var step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                if (step.Kind == StepKind.Send)
                {
                    await client.RecordPlayAsync(step.RatingKey!, step.Entry.LengthMs, ct).ConfigureAwait(false);
                    plays++;
                    sinceSave++;
                    archive?.AppendPlay(step, step.Entry.PlayedAt(Zone));
                    progress?.Report((plays, total));
                }

                processed[step.LogIndex] = step.Entry.EndOffset;
                dirty = true;
                if (sinceSave >= SaveEveryPlays)
                {
                    await SaveAsync().ConfigureAwait(false);
                    sinceSave = 0;
                }
            }

            plan.EndOffsets.ToArray().CopyTo(processed, 0); // past trailing headers and junk too
            dirty |= processed.Length > 0;

            foreach (var rating in plan.Ratings)
            {
                ct.ThrowIfCancellationRequested();
                await client.RateAsync(rating.RatingKey, rating.PlexValue, ct).ConfigureAwait(false);
                ratingsSent[rating.RatingKey] = rating.Rating;
                ratings++;
                dirty = true;
                progress?.Report((plays + ratings, total));
            }
        }
        catch (Exception ex) when (IsPlexFailure(ex, ct))
        {
            logger.LogWarning(ex, "Plex stopped accepting plays after {Plays} play(s) and {Ratings} rating(s)", plays, ratings);
            error = ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden }
                ? "Plex refused the plays (not allowed for this account on this server)."
                : "Couldn't reach Plex.";
        }
        finally
        {
            if (dirty)
            {
                await SaveAsync().ConfigureAwait(false);
            }
        }

        var result = new StatsResult(
            plays,
            ratings,
            plan.PlaysToSend - plays,
            plan.Ratings.Count - ratings,
            plan.Steps.Count(s => s.Kind == StepKind.TooShort),
            plan.Steps.Count(s => s.Kind == StepKind.NotOurs),
            error,
            []);
        logger.LogInformation("Stats return: sent {Plays} play(s) and {Ratings} rating(s), {Waiting} waiting", plays, ratings, result.PlaysWaiting + result.RatingsWaiting);
        return result with { Lines = Lines(result, preview.Hints) };

        // One bookmark per log read, plus the most recent old ones that aren't the same: a log that
        // couldn't be read this time (renamed by Rockbox meanwhile, or locked) keeps its bookmark, which
        // only ever matches the exact bytes it came from, so keeping stale ones is harmless.
        Task SaveAsync()
        {
            var current = StatsPlanner.Bookmarks(preview.Files.Logs, processed);
            var old = preview.State.Bookmarks
                .Where(o => !current.Any(c => c.PrefixHash == o.PrefixHash && c.ProcessedBytes == o.ProcessedBytes))
                .Take(MaxOldBookmarks);
            var state = preview.State with { Bookmarks = [.. current, .. old], RatingsSent = ratingsSent };
            return store.SaveStatsAsync(state, CancellationToken.None);
        }
    }

    /// <summary>
    /// "Send plays now" (D11): the same as a sync's step 0, without syncing. Uses PlexPod's records on
    /// the iPod; an iPod PlexPod has never synced has no tracks of its own, so nothing is sent.
    /// </summary>
    public async Task<StatsResult> SendNowAsync(
        string driveRoot,
        IPlexLibraryClient client,
        StatsOptions options,
        Func<string, bool> plexHasRating,
        CancellationToken ct,
        IProgress<(int Done, int Total)>? progress = null)
    {
        var store = new DeviceStore(driveRoot);
        var identity = await store.TryReadIdentityAsync(ct).ConfigureAwait(false);
        var (manifest, _) = await store.LoadManifestAsync(ct).ConfigureAwait(false);
        if (identity is null || manifest is null)
        {
            return new StatsResult(0, 0, 0, 0, 0, 0, null, ["PlexPod hasn't synced this iPod yet, so it has no plays to send."]);
        }

        var safe = Sync.SyncEngine.Sanitize(manifest, []);
        var preview = await PreviewAsync(driveRoot, safe, options, plexHasRating, ct).ConfigureAwait(false);
        var result = await SendAsync(preview, driveRoot, identity.DeviceId, client, ct, progress).ConfigureAwait(false);
        return result.Lines.Count > 0 ? result : result with { Lines = ["No new plays or ratings on the iPod."] };
    }

    /// <summary>"12 plays and 3 ratings".</summary>
    public static string Describe(int plays, int ratings) =>
        (plays, ratings) switch
        {
            (_, 0) => Plural(plays, "play"),
            (0, _) => Plural(ratings, "rating"),
            _ => $"{Plural(plays, "play")} and {Plural(ratings, "rating")}",
        };

    private static string Plural(int n, string noun) => string.Create(CultureInfo.CurrentCulture, $"{n:N0} {noun}{(n == 1 ? "" : "s")}");

    private static Dictionary<string, string> KeysByPath(DeviceManifest manifest)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in manifest.Tracks)
        {
            keys.TryAdd(track.DevicePath, track.RatingKey);
        }

        return keys;
    }

    private static bool IsPlexFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or PlexProtocolException
        || (ex is OperationCanceledException && !ct.IsCancellationRequested); // a request timeout, not Stop

    private static List<string> Hints(DeviceStatsFiles files, StatsPlan plan)
    {
        var hints = new List<string>();
        if (files.HasNoLog)
        {
            hints.Add("The iPod has no playback log, so no plays can be sent. To record them, turn on Settings → Playback Settings → Logging in Rockbox.");
        }
        else if (files.ChangelogLooksStale && plan.Steps.Count > 0)
        {
            hints.Add("To send ratings set on the iPod, choose Settings → General Settings → Database → Export Modifications in Rockbox before syncing.");
        }

        if (files.Skipped.Count > 0)
        {
            hints.Add($"Couldn't read {string.Join(", ", files.Skipped)} on the iPod; those plays will be tried again next time.");
        }

        return hints;
    }

    private static List<string> Lines(StatsResult r, IReadOnlyList<string> hints)
    {
        var lines = new List<string>();
        if (r.PlaysSent + r.RatingsSent > 0)
        {
            lines.Add($"Sent {Describe(r.PlaysSent, r.RatingsSent)} from the iPod to Plex.");
        }

        if (r.Error is not null)
        {
            lines.Add($"{r.Error} {Describe(r.PlaysWaiting, r.RatingsWaiting)} will be sent next time.");
        }

        if (r.TooShort > 0)
        {
            lines.Add($"Not counted: {Plural(r.TooShort, "play")} under 90% of the track.");
        }

        lines.AddRange(hints);
        return lines;
    }

    /// <summary>
    /// The PC-side history at <c>%LOCALAPPDATA%\PlexPod\stats\&lt;device id&gt;\</c>: copies of the logs read,
    /// and <c>plays.jsonl</c> with every play sent (real times, for Last.fm in M5). Best effort: a
    /// failure here never stops plays reaching Plex.
    /// </summary>
    private sealed class Archive
    {
        private readonly string _folder;
        private readonly ILogger _logger;

        private Archive(string folder, ILogger logger)
        {
            _folder = folder;
            _logger = logger;
        }

        public static Archive? TryOpen(string root, string deviceId, ILogger logger)
        {
            // The id comes from the iPod (untrusted): only a GUID is used as a folder name.
            var name = Guid.TryParse(deviceId, out var id)
                ? id.ToString("D")
                : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId ?? string.Empty)))[..32];
            try
            {
                var folder = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(folder, "logs"));
                return new Archive(folder, logger);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Couldn't create the stats archive");
                return null;
            }
        }

        public void KeepLogs(IReadOnlyList<LogFile> logs)
        {
            foreach (var log in logs)
            {
                Try(() =>
                {
                    var fingerprint = PlaybackLogParser.Fingerprint(log.Content);
                    var path = Path.Combine(_folder, "logs", $"{fingerprint}-{Path.GetFileName(log.FileName)}");
                    if (!File.Exists(path) || new FileInfo(path).Length != log.Content.Length)
                    {
                        File.WriteAllBytes(path, log.Content);
                    }
                });
            }
        }

        public void AppendPlay(PlayStep step, DateTimeOffset? playedAt) =>
            Try(() =>
            {
                var line = JsonSerializer.Serialize(new
                {
                    ratingKey = step.RatingKey,
                    path = step.Entry.DevicePath,
                    playedAt,
                    elapsedMs = step.Entry.ElapsedMs,
                    lengthMs = step.Entry.LengthMs,
                    sentAt = DateTimeOffset.Now,
                }, Json);
                File.AppendAllText(Path.Combine(_folder, "plays.jsonl"), line + "\n", new UTF8Encoding(false));
            });

        private void Try(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Couldn't update the stats archive");
            }
        }
    }
}
