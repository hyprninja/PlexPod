using System.Globalization;
using System.Text;

namespace PlexPod.Core.Tagging;

/// <summary>Plex values used for the folder layout, independent of <see cref="TagPolicy"/>.</summary>
public sealed record PathFields(string? AlbumArtist, int? Year, string? Album, int? Disc, int? Track, string? Title);

/// <summary>
/// Builds <c>/Music/&lt;AlbumArtist&gt;/&lt;Year&gt; - &lt;Album&gt;/&lt;Disc&gt;-&lt;Track:00&gt; &lt;Title&gt;.&lt;ext&gt;</c>
/// with FAT32-safe names (pipeline §4.3). Collision suffixes are M3's job (they need the manifest).
/// </summary>
public static class DevicePathBuilder
{
    public const string Root = "/Music";
    public const int MaxSegmentLength = 120;
    public const int MaxPathLength = 240;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Build(PathFields fields, string extension, bool isMultiDisc)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var ext = "." + extension.TrimStart('.').ToLowerInvariant();
        var artist = Sanitize(fields.AlbumArtist, "Unknown Artist");
        var albumName = Sanitize(fields.Album, "Unknown Album");
        var album = fields.Year is int y and > 0
            ? Sanitize(string.Create(CultureInfo.InvariantCulture, $"{y} - {albumName}"), albumName)
            : albumName;

        var prefix = fields.Track is int t and > 0
            ? (isMultiDisc && fields.Disc is int d and > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{d}-{t:00} ")
                : string.Create(CultureInfo.InvariantCulture, $"{t:00} "))
            : string.Empty;
        var title = Sanitize(fields.Title, "Untitled", MaxSegmentLength - prefix.Length - ext.Length);
        var file = prefix + title + ext;

        // Keep the full path within the limit: shorten the title, then the album, then the artist.
        var overflow = Root.Length + 3 + artist.Length + album.Length + file.Length - MaxPathLength;
        if (overflow > 0)
        {
            title = Shorten(title, Math.Max(8, title.Length - overflow));
            file = prefix + title + ext;
            overflow = Root.Length + 3 + artist.Length + album.Length + file.Length - MaxPathLength;
        }

        if (overflow > 0)
        {
            album = Shorten(album, Math.Max(8, album.Length - overflow));
            overflow = Root.Length + 3 + artist.Length + album.Length + file.Length - MaxPathLength;
        }

        if (overflow > 0)
        {
            artist = Shorten(artist, Math.Max(8, artist.Length - overflow));
        }

        return $"{Root}/{artist}/{album}/{file}";
    }

    /// <summary>One FAT32-safe path segment, NFC-normalised. Empty input becomes <paramref name="fallback"/>.</summary>
    public static string Sanitize(string? value, string fallback, int maxLength = MaxSegmentLength)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var builder = new StringBuilder();
        foreach (var c in (value ?? string.Empty).Normalize(NormalizationForm.FormC))
        {
            builder.Append(c switch
            {
                ':' => '-',
                '\\' or '/' or '*' or '?' or '"' or '<' or '>' or '|' => '_',
                _ when char.IsControl(c) => ' ',
                _ => c,
            });
        }

        var result = Shorten(CollapseSpaces(builder.ToString()), Math.Max(1, maxLength));
        if (result.Length == 0)
        {
            result = fallback;
        }

        // "CON", "nul.txt" etc. are unusable on FAT32 even with an extension.
        var stem = result.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? result + "_" : result;
    }

    private static string CollapseSpaces(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == ' ' && builder.Length > 0 && builder[^1] == ' ')
            {
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim().TrimEnd('.', ' ');
    }

    /// <summary>Cuts to at most <paramref name="max"/> UTF-16 units without splitting a surrogate pair.</summary>
    private static string Shorten(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        var cut = max;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut].TrimEnd('.', ' ');
    }
}
