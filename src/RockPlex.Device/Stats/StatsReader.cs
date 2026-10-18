using System.Globalization;
using System.Text;
using RockPlex.Core.Stats;

namespace RockPlex.Device.Stats;

/// <summary>What Rockbox has recorded on the iPod (M4 plan §1). Read-only: Rockbox's files are never changed (D9).</summary>
public sealed record DeviceStatsFiles(
    IReadOnlyList<LogFile> Logs,
    string? Changelog,
    DateTime? ChangelogWrittenUtc,
    DateTime? NewestLogWrittenUtc,
    IReadOnlyList<string> Skipped)
{
    /// <summary>No playback log at all: Logging is probably switched off on the iPod.</summary>
    public bool HasNoLog => Logs.Count == 0;

    /// <summary>The changelog is older than the newest play, so ratings set since then haven't been exported.</summary>
    public bool ChangelogLooksStale =>
        NewestLogWrittenUtc is { } log && (ChangelogWrittenUtc is not { } changelog || changelog < log);
}

/// <summary>Reads <c>/.rockbox/playback*.log</c> and <c>/.rockbox/database_changelog.txt</c>.</summary>
public static class StatsReader
{
    public const string RockboxFolder = ".rockbox";
    public const string LogName = "playback.log";
    public const string ChangelogName = "database_changelog.txt";

    /// <summary>Rockbox rotates at about 512 KB; anything far beyond that isn't a Rockbox log.</summary>
    internal const long MaxLogBytes = 32L << 20;

    internal const long MaxChangelogBytes = 64L << 20;

    internal const int MaxRotatedLogs = 1000;

    public static async Task<DeviceStatsFiles> ReadAsync(string driveRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        var folder = Path.Combine(driveRoot, RockboxFolder);
        var logs = new List<LogFile>();
        var skipped = new List<string>();
        DateTime? newestLog = null;
        if (!Directory.Exists(folder))
        {
            return new DeviceStatsFiles(logs, null, null, null, skipped);
        }

        foreach (var file in LogFiles(folder))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > MaxLogBytes)
            {
                skipped.Add(info.Name);
                continue;
            }

            if (await TryReadAsync(file, MaxLogBytes, ct).ConfigureAwait(false) is { } bytes)
            {
                logs.Add(new LogFile(info.Name, bytes));
                newestLog = Max(newestLog, info.LastWriteTimeUtc);
            }
            else
            {
                skipped.Add(info.Name);
            }
        }

        string? changelog = null;
        DateTime? changelogWritten = null;
        var changelogPath = Path.Combine(folder, ChangelogName);
        if (File.Exists(changelogPath) && await TryReadAsync(changelogPath, MaxChangelogBytes, ct).ConfigureAwait(false) is { } text)
        {
            changelog = new UTF8Encoding(false, false).GetString(text);
            changelogWritten = File.GetLastWriteTimeUtc(changelogPath);
        }

        return new DeviceStatsFiles(logs, changelog, changelogWritten, newestLog, skipped);
    }

    /// <summary>Rotated logs (<c>playback_0001.log</c>, … oldest first), then the current <c>playback.log</c>.</summary>
    internal static IEnumerable<string> LogFiles(string folder)
    {
        var rotated = new List<(int Number, string Path)>();
        foreach (var path in Directory.EnumerateFiles(folder, "playback_*.log"))
        {
            var stem = Path.GetFileNameWithoutExtension(path)["playback_".Length..];
            if (stem.Length is > 0 and <= 9 && stem.All(char.IsAsciiDigit)
                && int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                rotated.Add((number, path));
            }
        }

        foreach (var (_, path) in rotated.OrderBy(r => r.Number).Take(MaxRotatedLogs))
        {
            yield return path;
        }

        var current = Path.Combine(folder, LogName);
        if (File.Exists(current))
        {
            yield return current;
        }
    }

    /// <summary>Reads a file Rockbox may still have open; null when it can't be read now.</summary>
    private static async Task<byte[]?> TryReadAsync(string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                if (stream.Length > maxBytes)
                {
                    return null;
                }

                var buffer = new byte[stream.Length];
                await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
                return buffer;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? Max(DateTime? a, DateTime b) => a is { } x && x > b ? x : b;
}
