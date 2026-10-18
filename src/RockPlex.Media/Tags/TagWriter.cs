using System.Globalization;
using RockPlex.Core.Tagging;
using TagLib;
using TagLib.Ogg;
using TagFile = TagLib.File;

namespace RockPlex.Media.Tags;

public enum TagWriteOutcome
{
    Retagged,

    /// <summary>A format TagLib can't safely rewrite here (WAV, AIFF, WavPack, …): audio copied, tags left alone.</summary>
    NotRetagged,
}

/// <summary>What a file holds after writing, normalised to Vorbis keys, for verification and tests.</summary>
public sealed record TagReadBack(IReadOnlyList<KeyValuePair<string, string>> Fields, int PictureCount, TagTypes ForeignTags);

public sealed class TagWriteException(string message) : Exception(message);

/// <summary>
/// Device tags = wipe everything, then write one clean set (Rockbox's parser is first-value-wins for
/// text, so patching in place is unsafe). Works on the work copy only, never on a source file.
/// </summary>
public sealed class TagWriter
{
    private const string PictureField = "METADATA_BLOCK_PICTURE";
    private const string DashMean = "com.apple.iTunes";

    /// <summary>AAC encoder delay/padding for gapless playback: technical data, not metadata, so it survives the wipe.</summary>
    public const string GaplessInfoName = "iTunSMPB";

    // TagLib's BoxType is internal; the atom name is Latin-1 "©day".
    private static readonly ByteVector DayAtom = ByteVector.FromString("©day", StringType.Latin1);

    static TagWriter()
    {
        // ID3v2.4 with UTF-8 text for every frame RockPlex writes.
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
        TagLib.Id3v2.Tag.DefaultEncoding = StringType.UTF8;
        TagLib.Id3v2.Tag.ForceDefaultEncoding = true;
    }

    /// <summary>The only values carried over from the original (D3). Unreadable files yield none.</summary>
    public static SourceTags ReadSourceTags(string path)
    {
        try
        {
            using var file = TagFile.Create(path);
            var tag = file.Tag;
            return new SourceTags(tag.Composers.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)), tag.Comment);
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or CorruptFileException or IOException)
        {
            return SourceTags.None;
        }
    }

    /// <summary>The MP4 gapless atom (<c>iTunSMPB</c>), if the file has one.</summary>
    public static string? ReadGaplessInfo(string path)
    {
        try
        {
            using var file = TagFile.Create(path);
            return (file.GetTag(TagTypes.Apple, false) as TagLib.Mpeg4.AppleTag)?.GetDashBox(DashMean, GaplessInfoName);
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or CorruptFileException or IOException)
        {
            return null;
        }
    }

    /// <param name="gaplessInfo">MP4 only: an <c>iTunSMPB</c> value to write back after the wipe.</param>
    public static TagWriteOutcome Write(string path, TagSet tags, byte[]? coverJpeg, string? gaplessInfo = null)
    {
        ArgumentNullException.ThrowIfNull(tags);
        TagFile file;
        try
        {
            file = TagFile.Create(path);
        }
        catch (UnsupportedFormatException)
        {
            return TagWriteOutcome.NotRetagged;
        }

        using (file)
        {
            var picture = coverJpeg is null ? null : new Picture(new ByteVector(coverJpeg))
            {
                Type = PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = string.Empty,
            };

            switch (file)
            {
                case TagLib.Flac.File flac:
                    flac.RemoveTags(TagTypes.AllTags);
                    WriteXiph((XiphComment)flac.GetTag(TagTypes.Xiph, true), tags);
                    ((TagLib.Flac.Metadata)flac.GetTag(TagTypes.FlacMetadata, true)).Pictures = picture is null ? [] : [picture];
                    break;

                case TagLib.Ogg.File ogg:
                    ogg.RemoveTags(TagTypes.AllTags);
                    var xiph = (XiphComment)ogg.GetTag(TagTypes.Xiph, true);
                    WriteXiph(xiph, tags);
                    xiph.Pictures = picture is null ? [] : [picture];
                    break;

                case TagLib.Mpeg.AudioFile mp3:
                    mp3.RemoveTags(TagTypes.AllTags);
                    WriteId3((TagLib.Id3v2.Tag)mp3.GetTag(TagTypes.Id3v2, true), tags, picture);
                    break;

                case TagLib.Mpeg4.File mp4:
                    mp4.RemoveTags(TagTypes.AllTags);
                    var apple = (TagLib.Mpeg4.AppleTag)mp4.GetTag(TagTypes.Apple, true);
                    WriteApple(apple, tags, picture);
                    if (!string.IsNullOrWhiteSpace(gaplessInfo))
                    {
                        apple.SetDashBox(DashMean, GaplessInfoName, gaplessInfo);
                    }

                    break;

                default:
                    return TagWriteOutcome.NotRetagged;
            }

            file.Save();
        }

        Verify(path, tags, coverJpeg is null ? 0 : 1);
        return TagWriteOutcome.Retagged;
    }

    /// <summary>Re-reads the file and fails unless it holds exactly the intended tags and pictures.</summary>
    public static void Verify(string path, TagSet tags, int expectedPictures)
    {
        ArgumentNullException.ThrowIfNull(tags);
        var actual = ReadBack(path);
        var expected = Expected(path, tags);

        static string Line(KeyValuePair<string, string> f) => $"{f.Key}={f.Value}";
        var missing = expected.Select(Line).Except(actual.Fields.Select(Line)).ToList();
        var extra = actual.Fields.Select(Line).Except(expected.Select(Line)).ToList();
        if (missing.Count > 0 || extra.Count > 0 || actual.Fields.Count != expected.Count)
        {
            throw new TagWriteException($"Tags didn't read back as written. Missing: [{string.Join("; ", missing)}] Extra: [{string.Join("; ", extra)}]");
        }

        if (actual.PictureCount != expectedPictures)
        {
            throw new TagWriteException($"Expected {expectedPictures} embedded picture(s), found {actual.PictureCount}.");
        }

        if (actual.ForeignTags != TagTypes.None)
        {
            throw new TagWriteException($"Leftover tag blocks found: {actual.ForeignTags}.");
        }
    }

    /// <summary>Reads the tags back as (Vorbis key, value) pairs, whatever the container.</summary>
    public static TagReadBack ReadBack(string path)
    {
        using var file = TagFile.Create(path);
        var fields = new List<KeyValuePair<string, string>>();
        switch (file)
        {
            case TagLib.Flac.File flac:
                ReadXiph(flac.GetTag(TagTypes.Xiph, false) as XiphComment, fields);
                return new TagReadBack(fields, flac.GetTag(TagTypes.FlacMetadata, false)?.Pictures.Length ?? 0,
                    flac.TagTypesOnDisk & (TagTypes.Id3v1 | TagTypes.Id3v2 | TagTypes.Ape));

            case TagLib.Ogg.File ogg:
                var xiph = ogg.GetTag(TagTypes.Xiph, false) as XiphComment;
                ReadXiph(xiph, fields);
                return new TagReadBack(fields, xiph?.Pictures.Length ?? 0, TagTypes.None);

            case TagLib.Mpeg.AudioFile mp3:
                var id3 = (TagLib.Id3v2.Tag?)mp3.GetTag(TagTypes.Id3v2, false);
                if (id3 is not null)
                {
                    ReadId3(id3, fields);
                }

                return new TagReadBack(fields, id3?.Pictures.Length ?? 0, mp3.TagTypesOnDisk & (TagTypes.Id3v1 | TagTypes.Ape));

            case TagLib.Mpeg4.File mp4:
                var apple = (TagLib.Mpeg4.AppleTag?)mp4.GetTag(TagTypes.Apple, false);
                if (apple is not null)
                {
                    ReadApple(apple, fields);
                }

                return new TagReadBack(fields, apple?.Pictures.Length ?? 0, TagTypes.None);

            default:
                return new TagReadBack(fields, 0, TagTypes.None);
        }
    }

    /// <summary>
    /// What a container can hold: ID3 and MP4 store totals inside the number frame (TRCK "2/11",
    /// trkn), so a total without its number can't be written there.
    /// </summary>
    private static List<KeyValuePair<string, string>> Expected(string path, TagSet tags)
    {
        var fields = tags.ToFields().ToList();
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mp3" or ".m4a" or ".mp4" or ".aac" or ".alac")
        {
            if (tags.TrackNumber is null)
            {
                fields.RemoveAll(f => f.Key == TagSet.TrackTotalKey);
            }

            if (tags.DiscNumber is null)
            {
                fields.RemoveAll(f => f.Key == TagSet.DiscTotalKey);
            }
        }

        return fields;
    }

    private static void WriteXiph(XiphComment xiph, TagSet tags)
    {
        foreach (var field in xiph.ToList())
        {
            xiph.RemoveField(field);
        }

        foreach (var (key, value) in tags.ToFields())
        {
            xiph.SetField(key, value);
        }
    }

    private static void ReadXiph(XiphComment? xiph, List<KeyValuePair<string, string>> fields)
    {
        if (xiph is null)
        {
            return;
        }

        foreach (var key in xiph.Where(k => !k.Equals(PictureField, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var value in xiph.GetField(key))
            {
                fields.Add(new(key.ToUpperInvariant(), value));
            }
        }
    }

    private static void WriteId3(TagLib.Id3v2.Tag id3, TagSet tags, Picture? picture)
    {
        id3.Version = 4;
        id3.Title = tags.Title;
        id3.Performers = tags.Artist is null ? [] : [tags.Artist];
        id3.AlbumArtists = tags.AlbumArtist is null ? [] : [tags.AlbumArtist];
        id3.Album = tags.Album;
        id3.Track = (uint)(tags.TrackNumber ?? 0);
        id3.TrackCount = tags.TrackNumber is null ? 0 : (uint)(tags.TrackTotal ?? 0);
        id3.Disc = (uint)(tags.DiscNumber ?? 0);
        id3.DiscCount = tags.DiscNumber is null ? 0 : (uint)(tags.DiscTotal ?? 0);
        if (tags.Date is not null)
        {
            id3.SetTextFrame("TDRC", tags.Date);
        }

        id3.Genres = tags.Genre is null ? [] : [tags.Genre];
        id3.Grouping = tags.Grouping;
        id3.IsCompilation = tags.Compilation;
        id3.Publisher = tags.Label;
        id3.MusicBrainzTrackId = tags.MusicBrainzTrackId;
        id3.Composers = tags.Composer is null ? [] : [tags.Composer];
        id3.Comment = tags.Comment;
        SetTxxx(id3, TagSet.RatingKeyKey, tags.RockPlexRatingKey);
        SetTxxx(id3, TagSet.GuidKey, tags.RockPlexGuid);
        SetTxxx(id3, TagSet.PartIdKey, tags.RockPlexPartId.ToString(CultureInfo.InvariantCulture));
        id3.Pictures = picture is null ? [] : [picture];
    }

    private static void ReadId3(TagLib.Id3v2.Tag id3, List<KeyValuePair<string, string>> fields)
    {
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fields.Add(new(key, value));
            }
        }

        static string? N(uint n) => n == 0 ? null : n.ToString(CultureInfo.InvariantCulture);

        Add(TagSet.TitleKey, id3.Title);
        Add(TagSet.ArtistKey, id3.FirstPerformer);
        Add(TagSet.AlbumArtistKey, id3.FirstAlbumArtist);
        Add(TagSet.AlbumKey, id3.Album);
        Add(TagSet.TrackNumberKey, N(id3.Track));
        Add(TagSet.TrackTotalKey, N(id3.TrackCount));
        Add(TagSet.DiscNumberKey, N(id3.Disc));
        Add(TagSet.DiscTotalKey, N(id3.DiscCount));
        Add(TagSet.DateKey, id3.GetFrames<TagLib.Id3v2.TextInformationFrame>("TDRC").FirstOrDefault()?.Text.FirstOrDefault());
        Add(TagSet.GenreKey, id3.FirstGenre);
        Add(TagSet.GroupingKey, id3.Grouping);
        Add(TagSet.CompilationKey, id3.IsCompilation ? "1" : null);
        Add(TagSet.LabelKey, id3.Publisher);
        Add(TagSet.MusicBrainzTrackIdKey, id3.MusicBrainzTrackId);
        Add(TagSet.ComposerKey, id3.FirstComposer);
        Add(TagSet.CommentKey, id3.Comment);
        foreach (var key in new[] { TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey })
        {
            Add(key, TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false)?.Text.FirstOrDefault());
        }
    }

    private static void SetTxxx(TagLib.Id3v2.Tag id3, string description, string value) =>
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, true).Text = [value];

    private static void WriteApple(TagLib.Mpeg4.AppleTag apple, TagSet tags, Picture? picture)
    {
        apple.Title = tags.Title;
        apple.Performers = tags.Artist is null ? [] : [tags.Artist];
        apple.AlbumArtists = tags.AlbumArtist is null ? [] : [tags.AlbumArtist];
        apple.Album = tags.Album;
        apple.Track = (uint)(tags.TrackNumber ?? 0);
        apple.TrackCount = tags.TrackNumber is null ? 0 : (uint)(tags.TrackTotal ?? 0);
        apple.Disc = (uint)(tags.DiscNumber ?? 0);
        apple.DiscCount = tags.DiscNumber is null ? 0 : (uint)(tags.DiscTotal ?? 0);
        if (tags.Date is not null)
        {
            apple.SetText(DayAtom, tags.Date);
        }

        apple.Genres = tags.Genre is null ? [] : [tags.Genre];
        apple.Grouping = tags.Grouping;
        apple.IsCompilation = tags.Compilation;
        apple.MusicBrainzTrackId = tags.MusicBrainzTrackId;
        apple.Composers = tags.Composer is null ? [] : [tags.Composer];
        apple.Comment = tags.Comment;
        if (tags.Label is not null)
        {
            apple.SetDashBox(DashMean, TagSet.LabelKey, tags.Label);
        }

        apple.SetDashBox(DashMean, TagSet.RatingKeyKey, tags.RockPlexRatingKey);
        apple.SetDashBox(DashMean, TagSet.GuidKey, tags.RockPlexGuid);
        apple.SetDashBox(DashMean, TagSet.PartIdKey, tags.RockPlexPartId.ToString(CultureInfo.InvariantCulture));
        apple.Pictures = picture is null ? [] : [picture];
    }

    private static void ReadApple(TagLib.Mpeg4.AppleTag apple, List<KeyValuePair<string, string>> fields)
    {
        void Add(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                fields.Add(new(key, value));
            }
        }

        static string? N(uint n) => n == 0 ? null : n.ToString(CultureInfo.InvariantCulture);

        Add(TagSet.TitleKey, apple.Title);
        Add(TagSet.ArtistKey, apple.FirstPerformer);
        Add(TagSet.AlbumArtistKey, apple.FirstAlbumArtist);
        Add(TagSet.AlbumKey, apple.Album);
        Add(TagSet.TrackNumberKey, N(apple.Track));
        Add(TagSet.TrackTotalKey, N(apple.TrackCount));
        Add(TagSet.DiscNumberKey, N(apple.Disc));
        Add(TagSet.DiscTotalKey, N(apple.DiscCount));
        Add(TagSet.DateKey, apple.GetText(DayAtom).FirstOrDefault());
        Add(TagSet.GenreKey, apple.FirstGenre);
        Add(TagSet.GroupingKey, apple.Grouping);
        Add(TagSet.CompilationKey, apple.IsCompilation ? "1" : null);
        Add(TagSet.LabelKey, apple.GetDashBox(DashMean, TagSet.LabelKey));
        Add(TagSet.MusicBrainzTrackIdKey, apple.MusicBrainzTrackId);
        Add(TagSet.ComposerKey, apple.FirstComposer);
        Add(TagSet.CommentKey, apple.Comment);
        Add(TagSet.RatingKeyKey, apple.GetDashBox(DashMean, TagSet.RatingKeyKey));
        Add(TagSet.GuidKey, apple.GetDashBox(DashMean, TagSet.GuidKey));
        Add(TagSet.PartIdKey, apple.GetDashBox(DashMean, TagSet.PartIdKey));
    }
}
