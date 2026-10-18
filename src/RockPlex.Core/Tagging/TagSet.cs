using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RockPlex.Core.Tagging;

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
    public const string RatingKeyKey = "ROCKPLEX_RATINGKEY";
    public const string GuidKey = "ROCKPLEX_GUID";
    public const string PartIdKey = "ROCKPLEX_PARTID";

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

    public required string RockPlexRatingKey { get; init; }

    public required string RockPlexGuid { get; init; }

    public required long RockPlexPartId { get; init; }

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
        Add(RatingKeyKey, RockPlexRatingKey);
        Add(GuidKey, RockPlexGuid);
        Add(PartIdKey, RockPlexPartId.ToString(CultureInfo.InvariantCulture));
        return fields;
    }

    /// <summary>
    /// Stable hash of everything written. Because stripped fields are null, a policy change that
    /// alters what a file holds changes the hash, which is how M3 spots a tag-only rewrite.
    /// </summary>
    public string ContentHash()
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in ToFields())
        {
            builder.Append(key).Append('=').Append(value).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
