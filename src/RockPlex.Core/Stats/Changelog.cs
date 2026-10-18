using System.Globalization;
using System.Text;

namespace RockPlex.Core.Stats;

/// <summary>One modified track from Rockbox's <c>database_changelog.txt</c> (current values, not a delta).</summary>
public sealed record ChangelogEntry(IReadOnlyDictionary<string, string> Tags)
{
    public string? Filename => Tags.GetValueOrDefault("filename");

    /// <summary>0–10; Rockbox stores "never rated" as 0.</summary>
    public int? Rating => Number("rating");

    public int? PlayCount => Number("playcount");

    private int? Number(string key) =>
        Tags.TryGetValue(key, out var value) && int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
}

/// <summary>
/// Parses <c>## Changelog version 1</c> files: one line per track of <c>tag="value"</c> pairs, values
/// escaped with <c>\"</c>, <c>\\</c> and <c>\n</c> (apps/tagcache.c, write_tag).
/// </summary>
public static class ChangelogParser
{
    public static IReadOnlyList<ChangelogEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var entries = new List<ChangelogEntry>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var tags = ParseLine(line);
            if (tags.Count > 0)
            {
                entries.Add(new ChangelogEntry(tags));
            }
        }

        return entries;
    }

    internal static Dictionary<string, string> ParseLine(string line)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] == ' ')
            {
                i++;
            }

            var eq = line.IndexOf("=\"", i, StringComparison.Ordinal);
            if (eq <= i)
            {
                break;
            }

            var key = line[i..eq];
            var value = new StringBuilder();
            var j = eq + 2;
            var closed = false;
            while (j < line.Length)
            {
                var c = line[j];
                if (c == '\\' && j + 1 < line.Length)
                {
                    var next = line[j + 1];
                    value.Append(next == 'n' ? '\n' : next);
                    j += 2;
                    continue;
                }

                if (c == '"')
                {
                    closed = true;
                    j++;
                    break;
                }

                value.Append(c);
                j++;
            }

            if (!closed)
            {
                break; // truncated line: keep what parsed cleanly
            }

            tags[key] = value.ToString();
            i = j;
        }

        return tags;
    }
}
