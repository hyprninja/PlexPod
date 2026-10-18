using System.Buffers.Binary;
using PlexPod.Core.ItunesDb;

namespace PlexPod.Tests.Core;

public sealed class ItunesDbTests
{
    private static ItdbTrack Track(ulong dbid, string title, string? artist = "Sigur Rós", string? album = "Takk...", uint number = 1,
        ItdbFileKind kind = ItdbFileKind.AppleLossless, string? genre = "Post-Rock") => new()
    {
        Dbid = dbid,
        IpodPath = IpodPath.ToColon(IpodPath.ForTrack(dbid.ToString(System.Globalization.CultureInfo.InvariantCulture), kind == ItdbFileKind.Mp3 ? ".mp3" : ".m4a")),
        Title = title,
        Artist = artist,
        Album = album,
        AlbumArtist = artist,
        Genre = genre,
        Kind = kind,
        TrackNumber = number,
        TrackCount = 10,
        DiscNumber = 1,
        DiscCount = 1,
        Year = 2005,
        LengthMs = 268_000,
        SizeBytes = 31_000_000,
        Bitrate = 925,
        SampleRate = 44_100,
    };

    private static ItdbLibrary Library(IReadOnlyList<ItdbTrack> tracks, params ItdbPlaylist[] playlists) =>
        new(0x1122334455667788, 0x99AABBCCDDEEFF00, "IPOD", 0xABCDEF, tracks, playlists);

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var full = Track(7, "Hoppípolla") with
        {
            Composer = "Jónsi",
            Comment = "a comment",
            Grouping = "group",
            SortArtist = "Sigur Ros",
            SortAlbumArtist = "Sigur Ros",
            Compilation = true,
            Rating = 80,
            PlayCount = 12,
            SkipCount = 3,
            LastPlayed = new DateTime(2026, 10, 18, 9, 30, 0),
            LastSkipped = new DateTime(2026, 10, 17, 8, 0, 0),
            Added = new DateTime(2026, 10, 1, 12, 0, 0),
            Modified = new DateTime(2026, 10, 2, 12, 0, 0),
            Pregap = 2112,
            Postgap = 576,
            SampleCount = 11_818_800,
            GaplessData = 1234,
            GaplessTrack = true,
            GaplessAlbum = true,
        };
        var db = ItunesDbReader.Read(ItunesDbWriter.Write(Library([full, Track(8, "Glósóli", number: 2)])));

        Assert.Equal(full, db.Tracks[0].Track);
        Assert.Equal("Apple Lossless audio file", db.Tracks[0].FileTypeDescription);
        Assert.Equal(ItunesDbWriter.FirstTrackId, db.Tracks[0].Id);
        Assert.Equal(ItunesDbWriter.FirstTrackId + 1, db.Tracks[1].Id);
        Assert.Equal(0x1122334455667788UL, db.DatabaseId);
        Assert.Equal(0x99AABBCCDDEEFF00UL, db.LibraryId);
        Assert.Equal(ItunesDbWriter.Version, db.Version);
    }

    [Fact]
    public void Layout_MatchesLibgpod()
    {
        var bytes = ItunesDbWriter.Write(Library([Track(1, "A"), Track(2, "B", number: 2)], new ItdbPlaylist(5, "Road Trip", [2, 1, 2])));
        var db = ItunesDbReader.Read(bytes);

        Assert.Equal([1u, 3u, 2u, 4u, 8u, 6u, 10u, 5u], db.Sections);
        Assert.Equal(244u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        var mhit = bytes.AsSpan().IndexOf("mhit"u8);
        Assert.Equal(0x248u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mhit + 4)));
        Assert.Equal(1, db.AlbumCount);
        Assert.Equal(1, db.ArtistCount);

        var master = db.Playlists[0];
        Assert.True(master.IsMaster);
        Assert.Equal("IPOD", master.Name);
        Assert.Equal(5, master.SortIndexCount);
        Assert.Equal(db.Tracks.Select(t => t.Id), master.TrackIds);

        var road = Assert.Single(db.Playlists, p => !p.IsMaster);
        Assert.Equal("Road Trip", road.Name);
        Assert.Equal(5UL, road.Id);
        Assert.Equal([53u, 52u, 53u], road.TrackIds); // order and duplicates kept
        Assert.Equal(0, road.SortIndexCount);
    }

    [Fact]
    public void SameLibrary_SameBytes()
    {
        var library = Library([Track(1, "A"), Track(2, "B", number: 2)], new ItdbPlaylist(5, "P", [1]));
        Assert.Equal(ItunesDbWriter.Write(library), ItunesDbWriter.Write(library));
    }

    [Fact]
    public void EmptyLibrary_IsValid()
    {
        var db = ItunesDbReader.Read(ItunesDbWriter.Write(Library([])));
        Assert.Empty(db.Tracks);
        var master = Assert.Single(db.Playlists);
        Assert.True(master.IsMaster);
        Assert.Equal(0, master.SortIndexCount);
    }

    [Fact]
    public void Strings_UnicodeAndMissingFields()
    {
        var odd = Track(1, "日本語 — Ünïcødé 🎵", artist: null, album: null, genre: null);
        var back = Assert.Single(ItunesDbReader.Read(ItunesDbWriter.Write(Library([odd]))).Tracks).Track;
        Assert.Equal("日本語 — Ünïcødé 🎵", back.Title);
        Assert.Null(back.Artist);
        Assert.Null(back.Album);
    }

    [Fact]
    public void AlbumsAndArtists_GroupedAsLibgpodDoes()
    {
        var tracks = new[]
        {
            Track(1, "a", artist: "X", album: "Same"),
            Track(2, "b", artist: "Y", album: "Same"),    // same name, other artist: another album
            Track(3, "c", artist: "X", album: "Other"),
        };
        var db = ItunesDbReader.Read(ItunesDbWriter.Write(Library(tracks)));
        Assert.Equal(3, db.AlbumCount);
        Assert.Equal(2, db.ArtistCount);
    }

    [Fact]
    public void Writer_RefusesInconsistentInput()
    {
        Assert.Throws<ArgumentException>(() => ItunesDbWriter.Write(Library([Track(1, "a"), Track(1, "b")])));
        Assert.Throws<ArgumentException>(() => ItunesDbWriter.Write(Library([Track(1, "a")], new ItdbPlaylist(9, "P", [2]))));
        Assert.Throws<ArgumentException>(() => ItunesDbWriter.Write(Library([Track(1, "a") with { IpodPath = "/iPod_Control/Music/F00/A.m4a" }])));
    }

    [Fact]
    public void Reader_RejectsDamage()
    {
        var bytes = ItunesDbWriter.Write(Library([Track(1, "a"), Track(2, "b")], new ItdbPlaylist(5, "P", [1, 2])));
        Assert.Throws<ItunesDbFormatException>(() => ItunesDbReader.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<ItunesDbFormatException>(() => ItunesDbReader.Read(new byte[16]));

        var lying = (byte[])bytes.Clone();
        var mhit = lying.AsSpan().IndexOf("mhit"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(mhit + 8), 0x7FFFFFFF);
        Assert.Throws<ItunesDbFormatException>(() => ItunesDbReader.Read(lying));

        var orphan = (byte[])bytes.Clone();
        var mhip = orphan.AsSpan().LastIndexOf("mhip"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(orphan.AsSpan(mhip + 24), 999);
        Assert.Throws<ItunesDbFormatException>(() => ItunesDbReader.Read(orphan));
    }

    [Fact]
    public void ArtworkDb_RoundTrips_WithLibgpodsHeaderSizes()
    {
        var entries = new[]
        {
            new ArtworkEntry(0x64, 0x1122334455667788, 51_234,
            [
                new ArtworkThumb(1028, "F1028_1.ithmb", 0, 20_000, 100, 100, 0, 0),
                new ArtworkThumb(1029, "F1029_1.ithmb", 0, 80_000, 200, 200, 0, 0),
            ]),
            new ArtworkEntry(0x65, 42, 9_999,
            [
                new ArtworkThumb(1028, "F1028_1.ithmb", 20_000, 20_000, 82, 82, 18, 18),
                new ArtworkThumb(1029, "F1029_1.ithmb", 80_000, 80_000, 132, 132, 68, 68),
            ]),
        };
        var bytes = ArtworkDb.Write(entries, ArtworkFormat.IpodVideo);

        Assert.Equal(entries, ArtworkDb.Read(bytes), new ArtworkEntryComparer());
        Assert.Equal(0x84u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(0x65u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28))); // highest image id
        var mhii = bytes.AsSpan().IndexOf("mhii"u8);
        Assert.Equal(0x98u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mhii + 4)));
        var mhni = bytes.AsSpan().IndexOf("mhni"u8);
        Assert.Equal(0x4Cu, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(mhni + 4)));
        Assert.Equal(0, bytes.Length % 4);
        Assert.Equal(2, CountOf(bytes, "mhif"u8));
        Assert.Throws<ItunesDbFormatException>(() => ArtworkDb.Read(bytes.AsSpan(0, bytes.Length - 4)));
    }

    private static int CountOf(byte[] data, ReadOnlySpan<byte> tag)
    {
        var count = 0;
        for (var at = data.AsSpan().IndexOf(tag); at >= 0; at = data.AsSpan(at + 1).IndexOf(tag) is var next and >= 0 ? at + 1 + next : -1)
        {
            count++;
        }

        return count;
    }

    private sealed class ArtworkEntryComparer : IEqualityComparer<ArtworkEntry>
    {
        public bool Equals(ArtworkEntry? x, ArtworkEntry? y) =>
            x is not null && y is not null && x.ImageId == y.ImageId && x.SongDbid == y.SongDbid && x.SourceSize == y.SourceSize && x.Thumbs.SequenceEqual(y.Thumbs);

        public int GetHashCode(ArtworkEntry obj) => obj.ImageId.GetHashCode();
    }

    [Fact]
    public void TrackArtworkLink_RoundTrips()
    {
        var withArt = Track(1, "a") with { ArtworkId = 0x64, ArtworkSize = 51_234 };
        var back = ItunesDbReader.Read(ItunesDbWriter.Write(Library([withArt, Track(2, "b")]))).Tracks;
        Assert.Equal((0x64u, 51_234u), (back[0].Track.ArtworkId, back[0].Track.ArtworkSize));
        Assert.Equal((0u, 0u), (back[1].Track.ArtworkId, back[1].Track.ArtworkSize));
    }

    [Theory]
    [InlineData(":iPod_Control:Music:F03:ABCD.m4a", 0x4D344120u)] // "M4A "
    [InlineData(":iPod_Control:Music:F03:ABCD.mp3", 0x4D503320u)] // "MP3 "
    [InlineData(":iPod_Control:Music:F03:ABCD.aiff", 0x41494646u)] // "AIFF"
    public void FileTypeMarker_FromExtension(string path, uint marker) =>
        Assert.Equal(marker, ItunesDbWriter.FileTypeMarker(path));

    [Fact]
    public void MacTime_RoundTrips_AndZeroMeansNever()
    {
        var t = new DateTime(2026, 10, 18, 19, 30, 15);
        Assert.Equal(t, ItunesDbWriter.FromMacTime(ItunesDbWriter.ToMacTime(t)));
        Assert.Equal(0u, ItunesDbWriter.ToMacTime(null));
        Assert.Null(ItunesDbWriter.FromMacTime(0));
        Assert.Equal(3_875_196_615u, ItunesDbWriter.ToMacTime(t)); // Unix time + 2082844800, as libgpod converts
    }

    [Fact]
    public void IpodPaths()
    {
        Assert.Equal(":iPod_Control:Music:F03:ABCD.m4a", IpodPath.ToColon("/iPod_Control/Music/F03/ABCD.m4a"));
        Assert.Equal("/iPod_Control/Music/F03/ABCD.m4a", IpodPath.FromColon(":iPod_Control:Music:F03:ABCD.m4a"));
        Assert.Throws<ArgumentException>(() => IpodPath.ToColon("relative/path.m4a"));
        Assert.Throws<ArgumentException>(() => IpodPath.ToColon("/odd:name.m4a"));

        var path = IpodPath.ForTrack("48211", ".M4A");
        Assert.Matches(@"^/iPod_Control/Music/F[0-4]\d/[A-Z]{4}\.m4a$", path);
        Assert.Equal(path, IpodPath.ForTrack("48211", ".m4a"));
        Assert.NotEqual(path, IpodPath.ForTrack("48211", ".m4a", attempt: 1));
        Assert.True(IpodPath.IsMusicFile(path));
        Assert.False(IpodPath.IsMusicFile("/Music/A/b.flac"));

        var folders = Enumerable.Range(0, 2000).Select(i => IpodPath.ForTrack(i.ToString(System.Globalization.CultureInfo.InvariantCulture), ".m4a")[..23]).Distinct().Count();
        Assert.Equal(IpodPath.MusicFolderCount, folders);
    }

    [Fact]
    public void PlayCounts_RoundTrip_AndShortEntries()
    {
        var entries = new[]
        {
            new PlayCountEntry(3, new DateTime(2026, 10, 18, 8, 0, 0), 0, 60, 1, new DateTime(2026, 10, 18, 9, 0, 0)),
            new PlayCountEntry(0, null, 0, 0, 0, null),
        };
        Assert.Equal(entries, PlayCountsReader.Read(PlayCountsReader.Write(entries)));

        // Firmware 1.3 may write 12-byte entries: no rating.
        var old = new byte[0x60 + 12];
        "mhdp"u8.CopyTo(old);
        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(4), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(0x60), 5);
        var entry = Assert.Single(PlayCountsReader.Read(old));
        Assert.Equal(5u, entry.Plays);
        Assert.Null(entry.Rating);

        Assert.Throws<ItunesDbFormatException>(() => PlayCountsReader.Read("mhbd0000000000000000"u8));
        BinaryPrimitives.WriteUInt32LittleEndian(old.AsSpan(12), 2); // claims more entries than it holds
        Assert.Throws<ItunesDbFormatException>(() => PlayCountsReader.Read(old));
    }
}
