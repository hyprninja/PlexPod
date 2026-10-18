using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlexPod.Core.Tagging;

/// <summary>
/// The one clean set of tags written to an iPod copy. Null fields are not written at all.
/// No ReplayGain fields (owner decision D2).
/// </summary>
public sealed record TagSet
{
    // Vorbis comment keys. MP3/MP4 writers map these to their own frames and atoms.
    public const string TitleKey = "TITLE";
    public const string ArtistKey = "ARTIST";
    public const string AlbumArtistKey = "ALBUMARTIST";
    public const string AlbumKey = "ALBUM";
    public const string TrackNumberKey = "TRACKNUMBER";
    public const string TrackTotalKey = "TRACKTOTAL";
    public const string DiscNumberKey = "DISCNUMBER";
    public const string DiscTotalKey = "DISCTOTAL";
    public const string DateKey = "DATE";
    public const string GenreKey = "GENRE";
    public const string GroupingKey = "GROUPING";
    public const string CompilationKey = "COMPILATION";
    public const string LabelKey = "LABEL";
    public const string MusicBrainzTrackIdKey = "MUSICBRAINZ_TRACKID";
    public const string ComposerKey = "COMPOSER";
    public const string CommentKey = "COMMENT";
    public const string RatingKeyKey = "PLEXPOD_RATINGKEY";
    public const string GuidKey = "PLEXPOD_GUID";
    public const string PartIdKey = "PLEXPOD_PARTID";

    // The ID tags as written before the rebrand. Still recognised; a track gets the new ones whenever
    // it is next written.
    public const string LegacyRatingKeyKey = "ROCKPLEX_RATINGKEY";
    public const string LegacyGuidKey = "ROCKPLEX_GUID";
    public const string LegacyPartIdKey = "ROCKPLEX_PARTID";

    public string? Title { get; init; }

    public string? Artist { get; init; }

    public string? AlbumArtist { get; init; }

    public string? Album { get; init; }

    public int? TrackNumber { get; init; }

    public int? TrackTotal { get; init; }

    public int? DiscNumber { get; init; }

    public int? DiscTotal { get; init; }

    /// <summary>YYYY-MM-DD or YYYY. Rockbox reads the year from the front.</summary>
    public string? Date { get; init; }

    public string? Genre { get; init; }

    public string? Grouping { get; init; }

    public bool Compilation { get; init; }

    public string? Label { get; init; }

    public string? MusicBrainzTrackId { get; init; }

    public string? Composer { get; init; }

    public string? Comment { get; init; }

    public required string PlexPodRatingKey { get; init; }

    public required string PlexPodGuid { get; init; }

    public required long PlexPodPartId { get; init; }

    /// <summary>Every field that is written, as (Vorbis key, value), in a fixed order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ToFields()
    {
        var fields = new List<KeyValuePair<string, string>>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fields.Add(new(key, value));
            }
        }

        static string? Num(int? n) => n?.ToString(CultureInfo.InvariantCulture);

        Add(TitleKey, Title);
        Add(ArtistKey, Artist);
        Add(AlbumArtistKey, AlbumArtist);
        Add(AlbumKey, Album);
        Add(TrackNumberKey, Num(TrackNumber));
        Add(TrackTotalKey, Num(TrackTotal));
        Add(DiscNumberKey, Num(DiscNumber));
        Add(DiscTotalKey, Num(DiscTotal));
        Add(DateKey, Date);
        Add(GenreKey, Genre);
        Add(GroupingKey, Grouping);
        Add(CompilationKey, Compilation ? "1" : null);
        Add(LabelKey, Label);
        Add(MusicBrainzTrackIdKey, MusicBrainzTrackId);
        Add(ComposerKey, Composer);
        Add(CommentKey, Comment);
        Add(RatingKeyKey, PlexPodRatingKey);
        Add(GuidKey, PlexPodGuid);
        Add(PartIdKey, PlexPodPartId.ToString(CultureInfo.InvariantCulture));
        return fields;
    }

    /// <summary>
    /// Stable hash of everything written. Because stripped fields are null, a policy change that
    /// alters what a file holds changes the hash, which is how M3 spots a tag-only rewrite.
    /// </summary>
    public string ContentHash() => HashFields(ToFields());

    /// <summary>
    /// <see cref="ContentHash"/> as it was computed before the rebrand (ROCKPLEX_* ID keys). A file whose
    /// manifest hash equals this one holds exactly these tags under the old ID names: still current.
    /// </summary>
    public string LegacyContentHash() =>
        HashFields(ToFields().Select(f => f.Key switch
        {
            RatingKeyKey => new KeyValuePair<string, string>(LegacyRatingKeyKey, f.Value),
            GuidKey => new KeyValuePair<string, string>(LegacyGuidKey, f.Value),
            PartIdKey => new KeyValuePair<string, string>(LegacyPartIdKey, f.Value),
            _ => f,
        }));

    /// <summary>
    /// Tags read from a file with the pre-rebrand ID keys renamed to the current ones (dropped when the
    /// file has both), so an old file that is otherwise current hashes as current and isn't rewritten.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> WithCurrentIdKeys(IReadOnlyCollection<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var present = fields.Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            var current = field.Key.ToUpperInvariant() switch
            {
                LegacyRatingKeyKey => RatingKeyKey,
                LegacyGuidKey => GuidKey,
                LegacyPartIdKey => PartIdKey,
                _ => null,
            };

            if (current is null)
            {
                yield return field;
            }
            else if (!present.Contains(current))
            {
                yield return new KeyValuePair<string, string>(current, field.Value);
            }
        }
    }

    /// <summary>
    /// Order-independent hash of (key, value) pairs, so tags read back from a file hash the same as
    /// the set that was written. Keys are compared upper-case.
    /// </summary>
    public static string HashFields(IEnumerable<KeyValuePair<string, string>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var builder = new StringBuilder();
        foreach (var line in fields.Select(f => $"{f.Key.ToUpperInvariant()}={f.Value}").Order(StringComparer.Ordinal))
        {
            builder.Append(line).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
