using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RockPlex.Core.Stats;

/// <summary>
/// One line of Rockbox's playback log: <c>timestamp:elapsed_ms:length_ms:/path</c> (apps/playback.c).
/// <see cref="EndOffset"/> is the byte position just after this line, used as a bookmark.
/// </summary>
public sealed record PlaybackLogEntry(long Timestamp, long ElapsedMs, long LengthMs, string DevicePath, long EndOffset)
{
    /// <summary>
    /// The iPod's clock has no time zone, so its timestamps are local wall-clock time; this reads them in
    /// <paramref name="zone"/> (normally the PC's).
    /// </summary>
    public DateTimeOffset PlayedAt(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var wallClock = DateTime.SpecifyKind(DateTime.UnixEpoch.AddSeconds(Timestamp), DateTimeKind.Unspecified);
        return new DateTimeOffset(wallClock, zone.GetUtcOffset(wallClock));
    }
}

/// <summary>A parsed log, with the bytes consumed (complete lines only).</summary>
public sealed record ParsedPlaybackLog(IReadOnlyList<PlaybackLogEntry> Entries, long ConsumedBytes);

public static class PlaybackLogParser
{
    /// <summary>
    /// Parses complete lines from <paramref name="startOffset"/> on. A trailing line without a newline
    /// (still being written) is left for next time. Headers (<c>#</c>) and malformed lines are skipped.
    /// </summary>
    public static ParsedPlaybackLog Parse(ReadOnlySpan<byte> content, long startOffset = 0)
    {
        var entries = new List<PlaybackLogEntry>();
        var position = (int)Math.Clamp(startOffset, 0, content.Length);
        var consumed = position;
        while (position < content.Length)
        {
            var newline = content[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                break; // incomplete last line
            }

            var lineEnd = position + newline + 1;
            var line = Encoding.UTF8.GetString(content[position..(position + newline)]).TrimEnd('\r');
            if (TryParseLine(line, lineEnd, out var entry))
            {
                entries.Add(entry);
            }

            position = lineEnd;
            consumed = lineEnd;
        }

        return new ParsedPlaybackLog(entries, consumed);
    }

    /// <summary>Short hash of the first line, which never changes as the log grows or is renamed (names the PC-side copies).</summary>
    public static string Fingerprint(ReadOnlySpan<byte> content)
    {
        var newline = content.IndexOf((byte)'\n');
        var first = newline < 0 ? content : content[..newline];
        return Convert.ToHexStringLower(SHA256.HashData(first))[..16];
    }

    internal static bool TryParseLine(string line, long endOffset, out PlaybackLogEntry entry)
    {
        entry = null!;
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }

        // Three numbers, then the path (split at the first three colons only).
        var parts = line.Split(':', 4);
        if (parts.Length != 4
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp)
            || !long.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var elapsed)
            || !long.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length)
            || !parts[3].StartsWith('/'))
        {
            return false;
        }

        entry = new PlaybackLogEntry(timestamp, elapsed, length, parts[3], endOffset);
        return true;
    }
}

/// <summary>D8: a play counts only when at least 90% of the track was played.</summary>
public static class PlayRule
{
    public static bool Counts(PlaybackLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Integer form of elapsed >= 0.9 × length, so the boundary is exact.
        return entry.LengthMs > 0 && entry.ElapsedMs * 10 >= entry.LengthMs * 9;
    }
}
