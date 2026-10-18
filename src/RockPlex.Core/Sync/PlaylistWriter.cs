using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RockPlex.Core.Models;

namespace RockPlex.Core.Sync;

/// <summary>
/// Builds the <c>.m3u8</c> text for a playlist (selection doc §3): UTF-8, absolute <c>/Music/…</c> paths
/// taken from the manifest with exact case (P3), Plex order with duplicates kept (P2), and a
/// <c>#ROCKPLEX</c> header (P7). Entries without a file on the iPod are skipped and reported, never
/// written as broken lines (P1). No timestamp in the header, so unchanged playlists aren't rewritten.
/// </summary>
public static class PlaylistWriter
{
    public const string HeaderPrefix = "#ROCKPLEX ratingKey=";

    public static (string Content, IReadOnlyList<PlaylistEntry> Skipped) Build(
        SyncPlaylistTarget target,
        IReadOnlyDictionary<string, string> devicePathByRatingKey,
        Func<TrackItem, string> artistOf)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(devicePathByRatingKey);
        ArgumentNullException.ThrowIfNull(artistOf);

        var builder = new StringBuilder();
        builder.Append("#EXTM3U\n");
        builder.Append(HeaderPrefix).Append(target.Playlist.RatingKey).Append('\n');
        var skipped = new List<PlaylistEntry>();
        foreach (var entry in target.Contents.Entries)
        {
            if (entry.Unavailable || entry.Track is null || !devicePathByRatingKey.TryGetValue(entry.Track.RatingKey, out var path))
            {
                skipped.Add(entry);
                continue;
            }

            var seconds = (entry.Track.DurationMs / 1000).ToString(CultureInfo.InvariantCulture);
            builder.Append("#EXTINF:").Append(seconds).Append(',')
                .Append(OneLine(artistOf(entry.Track))).Append(" - ").Append(OneLine(entry.Track.Title)).Append('\n');
            builder.Append(path).Append('\n');
        }

        return (builder.ToString(), skipped);
    }

    public static string Hash(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>The rating key from a <c>#ROCKPLEX ratingKey=…</c> header in the first few lines, if any.</summary>
    public static string? ReadRatingKey(IEnumerable<string> firstLines)
    {
        ArgumentNullException.ThrowIfNull(firstLines);
        foreach (var line in firstLines.Take(5))
        {
            var trimmed = line.TrimStart('﻿').Trim();
            if (trimmed.StartsWith(HeaderPrefix, StringComparison.Ordinal))
            {
                var value = trimmed[HeaderPrefix.Length..].Split(' ', 2)[0];
                return value.Length > 0 ? value : null;
            }
        }

        return null;
    }

    /// <summary>The track paths in a playlist file's text (non-empty lines not starting with '#').</summary>
    public static IEnumerable<(int LineNumber, string Path)> TrackLines(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r').TrimStart('﻿');
            if (line.Length > 0 && line[0] != '#')
            {
                yield return (i + 1, line);
            }
        }
    }

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
