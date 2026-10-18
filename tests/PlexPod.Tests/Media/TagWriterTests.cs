using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlexPod.Core.Tagging;
using PlexPod.Media.Art;
using PlexPod.Media.Tags;
using TagLib;
using TagLib.Ogg;

namespace PlexPod.Tests.Media;

public sealed class TagWriterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly TagSet Tags = new()
    {
        Title = "Hoppípolla",
        Artist = "Sigur Rós",
        AlbumArtist = "Sigur Rós",
        Album = "Takk...",
        TrackNumber = 2,
        TrackTotal = 11,
        DiscNumber = 1,
        DiscTotal = 1,
        Date = "2005-09-12",
        Genre = "Post-Rock",
        Composer = "Jónsi",
        MusicBrainzTrackId = "0b5e0d3c-9f3e-4c5a-9c1e-2f4a6b8c0d1e",
        PlexPodRatingKey = "48211",
        PlexPodGuid = "plex://track/5d07cdd5",
        PlexPodPartId = 9001,
    };

    internal static byte[] Jpeg(int w = 64, int h = 64)
    {
        var pixels = new byte[w * h * 3];
        Array.Fill(pixels, (byte)0x80);
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Picture Pic() => new(new ByteVector(Jpeg())) { Type = PictureType.Other, MimeType = "image/jpeg" };

    /// <summary>Duplicate ARTIST (wrong first), junk fields, three pictures, plus ID3v2 glued on the front.</summary>
    private string MessyFlac()
    {
        var path = _dir.CopyFixture("tone-16-44.flac");
        using (var file = (TagLib.Flac.File)TagLib.File.Create(path))
        {
            var xiph = (XiphComment)file.GetTag(TagTypes.Xiph, true);
            xiph.SetField("ARTIST", "wrong", "right");
            xiph.SetField("ENCODEDBY", "Some Ripper 1.0");
            xiph.SetField("COMMENT", "visit www.junk.example");
            xiph.SetField("COMPOSER", "Old Composer");
            xiph.SetField("REPLAYGAIN_TRACK_GAIN", "-6.00 dB");
            ((TagLib.Flac.Metadata)file.GetTag(TagTypes.FlacMetadata, true)).Pictures = [Pic(), Pic(), Pic()];
            file.Save();
        }

        // A minimal ID3v2.3 tag with one TIT2 frame, glued onto the front as messy taggers do.
        byte[] frameText = [0, .. "Junk Title"u8];
        byte[] frame = [.. "TIT2"u8, 0, 0, 0, (byte)frameText.Length, 0, 0, .. frameText];
        byte[] id3 = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, (byte)frame.Length, .. frame];
        System.IO.File.WriteAllBytes(path, [.. id3, .. System.IO.File.ReadAllBytes(path)]);
        return path;
    }

    [Fact]
    public void MessyFlac_ComesOutWithExactlyOneCleanSet()
    {
        var path = MessyFlac();
        Assert.Equal("Old Composer", TagWriter.ReadSourceTags(path).Composer);

        var outcome = TagWriter.Write(path, Tags, Jpeg());

        Assert.Equal(TagWriteOutcome.Retagged, outcome);
        var back = TagWriter.ReadBack(path);
        Assert.Equal(Tags.ToFields().Select(f => $"{f.Key}={f.Value}").Order(), back.Fields.Select(f => $"{f.Key}={f.Value}").Order());
        Assert.Equal(1, back.PictureCount);
        Assert.Equal(TagTypes.None, back.ForeignTags);
        Assert.Single(back.Fields, f => f.Key == "ARTIST");
        Assert.DoesNotContain(back.Fields, f => f.Key is "ENCODEDBY" or "COMMENT" or "REPLAYGAIN_TRACK_GAIN");
        Assert.Equal((byte)'f', System.IO.File.ReadAllBytes(path)[0]);
    }

    [Fact]
    public void FlacPicture_IsFrontCover_Jpeg()
    {
        var path = MessyFlac();
        var art = ArtProcessor.Process(Jpeg(800, 600));
        TagWriter.Write(path, Tags, art.EmbeddedJpeg);
        using var file = TagLib.File.Create(path);
        var picture = Assert.Single(file.Tag.Pictures);
        Assert.Equal(PictureType.FrontCover, picture.Type);
        Assert.Equal("image/jpeg", picture.MimeType);
        Assert.Equal(art.EmbeddedJpeg, picture.Data.Data);
    }

    [Fact]
    public void EveryFieldOff_LeavesOnlyIdentityTags_AndNoPicture()
    {
        var path = MessyFlac();
        var bare = new TagSet { PlexPodRatingKey = "1", PlexPodGuid = "g", PlexPodPartId = 2 };
        TagWriter.Write(path, bare, null);
        var back = TagWriter.ReadBack(path);
        Assert.Equal(new[] { TagSet.RatingKeyKey, TagSet.GuidKey, TagSet.PartIdKey }.Order(), back.Fields.Select(f => f.Key).Order());
        Assert.Equal(0, back.PictureCount);
    }

    [Fact]
    public void Mp3_StripsOldTags_WritesId3v24_AndOnePicture()
    {
        var path = _dir.CopyFixture("tone.mp3");
        using (var file = TagLib.File.Create(path))
        {
            var v1 = file.GetTag(TagTypes.Id3v1, true);
            v1.Title = "v1 junk";
            var ape = file.GetTag(TagTypes.Ape, true);
            ape.Title = "ape junk";
            file.Tag.Pictures = [Pic(), Pic()];
            file.Save();
        }

        TagWriter.Write(path, Tags, Jpeg());

        var back = TagWriter.ReadBack(path);
        Assert.Equal(Tags.ToFields().Select(f => $"{f.Key}={f.Value}").Order(), back.Fields.Select(f => $"{f.Key}={f.Value}").Order());
        Assert.Equal(1, back.PictureCount);
        Assert.Equal(TagTypes.None, back.ForeignTags);
        using var check = (TagLib.Mpeg.AudioFile)TagLib.File.Create(path);
        Assert.Equal(4, ((TagLib.Id3v2.Tag)check.GetTag(TagTypes.Id3v2)).Version);
    }

    [Fact]
    public void M4a_WritesAppleTags_AndOneCover()
    {
        var path = _dir.CopyFixture("tone.m4a");
        TagWriter.Write(path, Tags with { Label = "EMI", Compilation = true, Grouping = "Dream Pop" }, Jpeg());
        var back = TagWriter.ReadBack(path);
        var expected = (Tags with { Label = "EMI", Compilation = true, Grouping = "Dream Pop" }).ToFields();
        Assert.Equal(expected.Select(f => $"{f.Key}={f.Value}").Order(), back.Fields.Select(f => $"{f.Key}={f.Value}").Order());
        Assert.Equal(1, back.PictureCount);
    }

    [Fact]
    public void Mp3_TotalWithoutNumber_IsDroppedConsistently()
    {
        var path = _dir.CopyFixture("tone.mp3");
        var tags = Tags with { TrackNumber = null };
        TagWriter.Write(path, tags, null);
        Assert.DoesNotContain(TagWriter.ReadBack(path).Fields, f => f.Key is TagSet.TrackNumberKey or TagSet.TrackTotalKey);
    }

    [Fact]
    public void UnsupportedFormat_IsNotRetagged()
    {
        var path = _dir.File("noise.xyz");
        System.IO.File.WriteAllBytes(path, new byte[100]);
        Assert.Equal(TagWriteOutcome.NotRetagged, TagWriter.Write(path, Tags, null));
    }

    [Fact]
    public void Verify_FailsWhenAFieldIsMissing()
    {
        var path = _dir.CopyFixture("tone-16-44.flac");
        TagWriter.Write(path, Tags, null);
        Assert.Throws<TagWriteException>(() => TagWriter.Verify(path, Tags with { Comment = "expected but absent" }, 0));
        Assert.Throws<TagWriteException>(() => TagWriter.Verify(path, Tags, 1));
    }
}
