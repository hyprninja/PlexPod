using System.Globalization;
using PlexPod.Core.Tagging;
using TagLib;
using TagLib.Ogg;
using TagFile = TagLib.File;

namespace PlexPod.Media.Tags;

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

    // MP4 text atoms (TagLib# keeps its own names internal).
    private static readonly ByteVector NamAtom = ByteVector.FromString("©nam", StringType.Latin1);
    private static readonly ByteVector ArtAtom = ByteVector.FromString("©ART", StringType.Latin1);
    private static readonly ByteVector AartAtom = ByteVector.FromString("aART", StringType.Latin1);
    private static readonly ByteVector AlbAtom = ByteVector.FromString("©alb", StringType.Latin1);
    private static readonly ByteVector GenAtom = ByteVector.FromString("©gen", StringType.Latin1);
    private static readonly ByteVector GrpAtom = ByteVector.FromString("©grp", StringType.Latin1);
    private static readonly ByteVector WrtAtom = ByteVector.FromString("©wrt", StringType.Latin1);
    private static readonly ByteVector CmtAtom = ByteVector.FromString("©cmt", StringType.Latin1);

    static TagWriter()
    {
        // ID3v2.4 with UTF-8 text for every frame PlexPod writes.
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

    /// <summary>Is this MP4 file's audio Apple Lossless (rather than AAC)?</summary>
    public static bool IsAppleLossless(string path)
    {
        try
        {
            using var file = TagFile.Create(path);
            return file.Properties.Codecs.Any(c => c?.Description?.Contains("alac", StringComparison.OrdinalIgnoreCase) == true
                || c?.Description?.Contains("Apple Lossless", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or CorruptFileException or IOException)
        {
            return false;
        }
    }

    /// <summary>Length (ms), bitrate (kbit/s) and sample rate (Hz) as TagLib reads them; zeros when it can't.</summary>
    public static (uint LengthMs, uint Bitrate, uint SampleRate) ReadAudio(string path)
    {
        try
        {
            using var file = TagFile.Create(path);
            var p = file.Properties;
            return ((uint)Math.Max(0, p.Duration.TotalMilliseconds), (uint)Math.Max(0, p.AudioBitrate), (uint)Math.Max(0, p.AudioSampleRate));
        }
        catch (Exception ex) when (ex is UnsupportedFormatException or CorruptFileException or IOException)
        {
            return (0, 0, 0);
        }
    }

    /// <summary>
    /// Parses an <c>iTunSMPB</c> value (" 00000000 00000840 000001C0 0000000000003A00": encoder delay,
    /// padding, sample count in hex) for the stock firmware's gapless fields.
    /// </summary>
    public static (uint Pregap, uint Postgap, ulong SampleCount)? ParseGaplessInfo(string? value)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 4 }
            || !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var pre)
            || !uint.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var post)
            || !ulong.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out var samples)
            || samples == 0)
        {
            return null;
        }

        return (pre, post, samples);
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

    private static readonly HashSet<string> WrittenExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".m4b", ".mp4", ".ogg", ".oga", ".opus",
    };

    /// <summary>Formats <see cref="Write"/> rewrites; anything else is copied with its tags as they are.</summary>
    public static bool Writes(string extension) => WrittenExtensions.Contains(extension);

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
        SetTxxx(id3, TagSet.RatingKeyKey, tags.PlexPodRatingKey);
        SetTxxx(id3, TagSet.GuidKey, tags.PlexPodGuid);
        SetTxxx(id3, TagSet.PartIdKey, tags.PlexPodPartId.ToString(CultureInfo.InvariantCulture));
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
        foreach (var key in new[] { TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey, TagSet.LegacyRatingKeyKey, TagSet.LegacyGuidKey, TagSet.LegacyPartIdKey })
        {
            Add(key, TagLib.Id3v2.UserTextInformationFrame.Get(id3, key, false)?.Text.FirstOrDefault());
        }
    }

    private static void SetTxxx(TagLib.Id3v2.Tag id3, string description, string value) =>
        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, true).Text = [value];

    private static void WriteApple(TagLib.Mpeg4.AppleTag apple, TagSet tags, Picture? picture)
    {
        AppleText(apple, NamAtom, tags.Title);
        AppleText(apple, ArtAtom, tags.Artist);
        AppleText(apple, AartAtom, tags.AlbumArtist);
        AppleText(apple, AlbAtom, tags.Album);
        apple.Track = (uint)(tags.TrackNumber ?? 0);
        apple.TrackCount = tags.TrackNumber is null ? 0 : (uint)(tags.TrackTotal ?? 0);
        apple.Disc = (uint)(tags.DiscNumber ?? 0);
        apple.DiscCount = tags.DiscNumber is null ? 0 : (uint)(tags.DiscTotal ?? 0);
        if (tags.Date is not null)
        {
            AppleText(apple, DayAtom, tags.Date);
        }

        AppleText(apple, GenAtom, tags.Genre);
        AppleText(apple, GrpAtom, tags.Grouping);
        apple.IsCompilation = tags.Compilation;
        apple.MusicBrainzTrackId = tags.MusicBrainzTrackId;
        AppleText(apple, WrtAtom, tags.Composer);
        AppleText(apple, CmtAtom, tags.Comment);
        if (tags.Label is not null)
        {
            apple.SetDashBox(DashMean, TagSet.LabelKey, tags.Label);
        }

        apple.SetDashBox(DashMean, TagSet.RatingKeyKey, tags.PlexPodRatingKey);
        apple.SetDashBox(DashMean, TagSet.GuidKey, tags.PlexPodGuid);
        apple.SetDashBox(DashMean, TagSet.PartIdKey, tags.PlexPodPartId.ToString(CultureInfo.InvariantCulture));
        apple.Pictures = picture is null ? [] : [picture];
    }

    /// <summary>
    /// One text atom holding exactly <paramref name="value"/>. TagLib#'s own text setters split on ';'
    /// into several values (the iPod then shows only the first: "PetroDragonic Apocalypse" for
    /// "PetroDragonic Apocalypse; or, Dawn of …"), so the data is written directly.
    /// </summary>
    private static void AppleText(TagLib.Mpeg4.AppleTag apple, ByteVector atom, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            apple.ClearData(atom);
            return;
        }

        apple.SetData(atom, new ByteVectorCollection { ByteVector.FromString(value, StringType.UTF8) }, (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsText);
    }

    /// <summary>The first text value of an atom, whole: TagLib#'s getters split it on ';' like its setters.</summary>
    private static string? AppleRaw(TagLib.Mpeg4.AppleTag apple, ByteVector atom) =>
        apple.DataBoxes(atom).FirstOrDefault(b => b.Flags == (uint)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsText)?.Text;

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

        Add(TagSet.TitleKey, AppleRaw(apple, NamAtom));
        Add(TagSet.ArtistKey, AppleRaw(apple, ArtAtom));
        Add(TagSet.AlbumArtistKey, AppleRaw(apple, AartAtom));
        Add(TagSet.AlbumKey, AppleRaw(apple, AlbAtom));
        Add(TagSet.TrackNumberKey, N(apple.Track));
        Add(TagSet.TrackTotalKey, N(apple.TrackCount));
        Add(TagSet.DiscNumberKey, N(apple.Disc));
        Add(TagSet.DiscTotalKey, N(apple.DiscCount));
        Add(TagSet.DateKey, AppleRaw(apple, DayAtom));
        Add(TagSet.GenreKey, AppleRaw(apple, GenAtom));
        Add(TagSet.GroupingKey, AppleRaw(apple, GrpAtom));
        Add(TagSet.CompilationKey, apple.IsCompilation ? "1" : null);
        Add(TagSet.LabelKey, apple.GetDashBox(DashMean, TagSet.LabelKey));
        Add(TagSet.MusicBrainzTrackIdKey, apple.MusicBrainzTrackId);
        Add(TagSet.ComposerKey, AppleRaw(apple, WrtAtom));
        Add(TagSet.CommentKey, AppleRaw(apple, CmtAtom));
        foreach (var key in new[] { TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey, TagSet.LegacyRatingKeyKey, TagSet.LegacyGuidKey, TagSet.LegacyPartIdKey })
        {
            Add(key, apple.GetDashBox(DashMean, key));
        }
    }
}
