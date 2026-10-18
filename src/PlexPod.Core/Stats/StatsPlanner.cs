using System.Security.Cryptography;

namespace PlexPod.Core.Stats;

/// <summary>
/// How far a log has been read: the bytes processed and a hash of exactly those bytes. Logs only
/// grow, and a rotated log keeps its content, so a log whose first <see cref="ProcessedBytes"/> bytes
/// hash the same is that log, whatever it's called now; any other file is read from the start.
/// </summary>
public sealed record LogBookmark(long ProcessedBytes, string PrefixHash, string FileName)
{
    public static LogBookmark At(LogFile log, long processed)
    {
        ArgumentNullException.ThrowIfNull(log);
        processed = Math.Clamp(processed, 0, log.Content.Length);
        return new LogBookmark(processed, HashPrefix(log.Content, processed), log.FileName);
    }

    public bool Matches(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return ProcessedBytes > 0 && ProcessedBytes <= content.Length && HashPrefix(content, ProcessedBytes) == PrefixHash;
    }

    private static string HashPrefix(byte[] content, long length) =>
        Convert.ToHexStringLower(SHA256.HashData(content.AsSpan(0, (int)length)));
}

/// <summary>What PlexPod remembers on the iPod between syncs (<c>/.plexpod/stats.json</c>).</summary>
public sealed record StatsState
{
    public IReadOnlyList<LogBookmark> Bookmarks { get; init; } = [];

    /// <summary>Last rating sent to Plex per track rating key (0–10).</summary>
    public IReadOnlyDictionary<string, int> RatingsSent { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// Stock firmware (M6): plays taken from the iPod's Play Counts that Plex hasn't accepted yet, per
    /// track rating key. Play Counts is deleted once read, so these are the only record until sent.
    /// </summary>
    public IReadOnlyDictionary<string, int> PendingPlays { get; init; } = new Dictionary<string, int>();

    /// <summary>Stock firmware: ratings changed on the iPod (0–10, or -1 for cleared) not yet accepted by Plex.</summary>
    public IReadOnlyDictionary<string, int> PendingRatings { get; init; } = new Dictionary<string, int>();

    public static StatsState Empty { get; } = new();
}

public enum RatingMode
{
    /// <summary>A rating changed on the iPod replaces Plex's.</summary>
    IpodWins,

    /// <summary>Only sent when the track has no rating in Plex.</summary>
    FillGapsOnly,
}

/// <summary>A log file as read from the iPod. Numbered rotated logs come before <c>playback.log</c>.</summary>
public sealed record LogFile(string FileName, byte[] Content);

public enum StepKind
{
    Send,

    /// <summary>Under the 90% rule.</summary>
    TooShort,

    /// <summary>A file PlexPod doesn't own (the user's own music).</summary>
    NotOurs,
}

/// <summary>One log line in order; sending (or skipping) it advances log <see cref="LogIndex"/> to <see cref="PlaybackLogEntry.EndOffset"/>.</summary>
public sealed record PlayStep(StepKind Kind, PlaybackLogEntry Entry, int LogIndex, string? RatingKey);

public sealed record RatingToSend(string RatingKey, string DevicePath, int Rating)
{
    /// <summary>Plex uses -1 to clear a rating; Rockbox's 0 means unrated.</summary>
    public double PlexValue => Rating == 0 ? -1 : Rating;
}

/// <param name="StartOffsets">Per log, where reading began (its bookmark, or 0).</param>
/// <param name="EndOffsets">Per log, the end of its last complete line: where it stands once every step is done.</param>
public sealed record StatsPlan(
    IReadOnlyList<PlayStep> Steps,
    IReadOnlyList<RatingToSend> Ratings,
    IReadOnlyList<long> StartOffsets,
    IReadOnlyList<long> EndOffsets)
{
    public int PlaysToSend => Steps.Count(s => s.Kind == StepKind.Send);

    public bool IsEmpty => PlaysToSend == 0 && Ratings.Count == 0;
}

/// <summary>
/// Works out which plays and ratings to send (M4 plan §2–§4). Pure: the caller reads the files and
/// supplies the manifest's path → rating key map.
/// </summary>
public static class StatsPlanner
{
    public static StatsPlan Plan(
        IReadOnlyList<LogFile> logs,
        IReadOnlyList<ChangelogEntry> changelog,
        StatsState state,
        IReadOnlyDictionary<string, string> ratingKeyByDevicePath,
        RatingMode mode,
        Func<string, bool> plexHasRating)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(changelog);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(ratingKeyByDevicePath);
        ArgumentNullException.ThrowIfNull(plexHasRating);

        var paths = new Dictionary<string, string>(ratingKeyByDevicePath, StringComparer.OrdinalIgnoreCase);
        var unused = state.Bookmarks.Where(b => b is not null).OrderByDescending(b => b.ProcessedBytes).ToList();
        var steps = new List<PlayStep>();
        var starts = new List<long>();
        var ends = new List<long>();

        for (var i = 0; i < logs.Count; i++)
        {
            var log = logs[i];

            // Each bookmark belongs to at most one log. No match: a new (or replaced) log, read in full.
            var mark = unused.FirstOrDefault(b => b.Matches(log.Content));
            if (mark is not null)
            {
                unused.Remove(mark);
            }

            var start = mark?.ProcessedBytes ?? 0;
            var parsed = PlaybackLogParser.Parse(log.Content, start);
            foreach (var entry in parsed.Entries)
            {
                var key = paths.GetValueOrDefault(entry.DevicePath);
                var kind = key is null ? StepKind.NotOurs : PlayRule.Counts(entry) ? StepKind.Send : StepKind.TooShort;
                steps.Add(new PlayStep(kind, entry, i, key));
            }

            starts.Add(start);
            ends.Add(parsed.ConsumedBytes);
        }

        return new StatsPlan(steps, PlanRatings(changelog, state, paths, mode, plexHasRating), starts, ends);
    }

    /// <summary>The bookmarks to save once each log has been processed up to <paramref name="processed"/>.</summary>
    public static List<LogBookmark> Bookmarks(IReadOnlyList<LogFile> logs, IReadOnlyList<long> processed)
    {
        ArgumentNullException.ThrowIfNull(logs);
        ArgumentNullException.ThrowIfNull(processed);
        return [.. logs.Select((log, i) => LogBookmark.At(log, processed[i])).Where(b => b.ProcessedBytes > 0)];
    }

    private static List<RatingToSend> PlanRatings(
        IReadOnlyList<ChangelogEntry> changelog,
        StatsState state,
        Dictionary<string, string> paths,
        RatingMode mode,
        Func<string, bool> plexHasRating)
    {
        var ratings = new List<RatingToSend>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in changelog)
        {
            if (entry.Filename is not { } file || entry.Rating is not int rating || rating is < 0 or > 10
                || !paths.TryGetValue(file, out var key) || !seen.Add(key))
            {
                continue;
            }

            var hasSent = state.RatingsSent.TryGetValue(key, out var lastSent);
            if ((hasSent && lastSent == rating) || (!hasSent && rating == 0))
            {
                continue; // unchanged, or never rated (Rockbox's 0 must not wipe a Plex rating)
            }

            if (mode == RatingMode.FillGapsOnly && plexHasRating(key))
            {
                continue;
            }

            ratings.Add(new RatingToSend(key, file, rating));
        }

        return ratings;
    }
}
