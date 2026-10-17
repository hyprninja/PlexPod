namespace RockPlex.Device;

/// <summary>Parsed <c>/.rockbox/rockbox-info.txt</c>. Keys are case-insensitive.</summary>
public sealed record RockboxInfo(IReadOnlyDictionary<string, string> Values)
{
    public static readonly RockboxInfo Empty = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public string? Target => Values.GetValueOrDefault("Target");

    public string? Version => Values.GetValueOrDefault("Version");

    public string? Memory => Values.GetValueOrDefault("Memory");
}

/// <summary>
/// Lenient <c>key: value</c> parser ⚠️ (check against a real file). Lines without a colon, or with an
/// empty key, are ignored; the first occurrence of a key wins.
/// </summary>
public static class RockboxInfoParser
{
    public static RockboxInfo Parse(string? text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text))
        {
            return new RockboxInfo(values);
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('﻿');
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Length > 0)
            {
                values.TryAdd(key, value);
            }
        }

        return new RockboxInfo(values);
    }
}
