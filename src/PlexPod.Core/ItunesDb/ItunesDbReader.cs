using System.Buffers.Binary;
using System.Text;

namespace PlexPod.Core.ItunesDb;

/// <summary>A track as read back, with the id playlists and Play Counts refer to it by.</summary>
public sealed record ItdbReadTrack(uint Id, ItdbTrack Track, string? FileTypeDescription, uint FileTypeMarker);

public sealed record ItdbReadPlaylist(ulong Id, string Name, bool IsMaster, IReadOnlyList<uint> TrackIds, int SortIndexCount);

/// <param name="Sections">mhsd types in file order.</param>
public sealed record ItdbReadResult(
    uint Version,
    ulong DatabaseId,
    ulong LibraryId,
    IReadOnlyList<uint> Sections,
    IReadOnlyList<ItdbReadTrack> Tracks,
    IReadOnlyList<ItdbReadPlaylist> Playlists,
    int AlbumCount,
    int ArtistCount);

/// <summary>
/// Reads an <c>iTunesDB</c>: used to check every rebuild before it replaces the old one, and to line
/// up the firmware's Play Counts with the database it was recorded against. Strict: any length that
/// doesn't fit throws <see cref="ItunesDbFormatException"/> rather than returning half a database.
/// The playlists come from the type-2 section.
/// </summary>
public static class ItunesDbReader
{
    public static ItdbReadResult Read(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        r.Expect(0, "mhbd");
        var header = r.U32(4);
        var total = r.U32(8);
        if (header < 0x68 || total != data.Length)
        {
            throw new ItunesDbFormatException($"mhbd says {total} bytes with a {header}-byte header; the file is {data.Length}.");
        }

        var version = r.U32(16);
        var children = r.U32(20);
        var databaseId = r.U64(24);
        var libraryId = header >= 0x50 ? r.U64(0x48) : 0;

        var sections = new List<uint>();
        var tracks = new List<ItdbReadTrack>();
        var playlists = new List<ItdbReadPlaylist>();
        int albums = 0, artists = 0;
        var at = (int)header;
        for (var i = 0; i < children; i++)
        {
            r.Expect(at, "mhsd");
            var sectionHeader = r.U32(at + 4);
            var sectionTotal = r.U32(at + 8);
            var type = r.U32(at + 12);
            var end = r.End(at, sectionTotal);
            sections.Add(type);
            var list = at + (int)sectionHeader;
            switch (type)
            {
                case 1:
                    r.Expect(list, "mhlt");
                    ReadTracks(r, list + (int)r.U32(list + 4), r.U32(list + 8), end, tracks);
                    break;
                case 2:
                    r.Expect(list, "mhlp");
                    ReadPlaylists(r, list + (int)r.U32(list + 4), r.U32(list + 8), end, playlists);
                    break;
                case 4:
                    r.Expect(list, "mhla");
                    albums = (int)r.U32(list + 8);
                    break;
                case 8:
                    r.Expect(list, "mhli");
                    artists = (int)r.U32(list + 8);
                    break;
            }

            at = end;
        }

        if (at != data.Length)
        {
            throw new ItunesDbFormatException($"{data.Length - at} bytes after the last section.");
        }

        var known = tracks.Select(t => t.Id).ToHashSet();
        foreach (var playlist in playlists)
        {
            if (playlist.TrackIds.FirstOrDefault(id => !known.Contains(id)) is var missing and not 0)
            {
                throw new ItunesDbFormatException($"Playlist \"{playlist.Name}\" refers to track {missing}, which isn't in the database.");
            }
        }

        return new ItdbReadResult(version, databaseId, libraryId, sections, tracks, playlists, albums, artists);
    }

    private static void ReadTracks(Reader r, int at, uint count, int end, List<ItdbReadTrack> tracks)
    {
        for (var i = 0; i < count; i++)
        {
            r.Expect(at, "mhit");
            var header = r.U32(at + 4);
            var total = r.U32(at + 8);
            var mhods = r.U32(at + 12);
            var next = r.End(at, total, end);
            if (header < 0x9C)
            {
                throw new ItunesDbFormatException($"Track header of {header} bytes is too short.");
            }

            var strings = ReadStrings(r, at + (int)header, mhods, next);
            string? S(uint type) => strings.GetValueOrDefault(type);
            var description = S(6);
            var marker = r.U32(at + 0x18);
            var path = S(2) ?? throw new ItunesDbFormatException($"Track {r.U32(at + 0x10)} has no path.");
            var track = new ItdbTrack
            {
                Dbid = r.U64(at + 0x70),
                IpodPath = path,
                Title = S(1) ?? string.Empty,
                Artist = S(4),
                Album = S(3),
                AlbumArtist = S(22),
                Genre = S(5),
                Composer = S(12),
                Comment = S(8),
                Grouping = S(13),
                SortArtist = S(23),
                SortAlbumArtist = S(29),
                Kind = KindOf(description, path),
                Compilation = r.U8(at + 0x1E) != 0,
                Rating = r.U8(at + 0x1F),
                Modified = ItunesDbWriter.FromMacTime(r.U32(at + 0x20)),
                SizeBytes = r.U32(at + 0x24),
                LengthMs = r.U32(at + 0x28),
                TrackNumber = r.U32(at + 0x2C),
                TrackCount = r.U32(at + 0x30),
                Year = r.U32(at + 0x34),
                Bitrate = r.U32(at + 0x38),
                SampleRate = r.U32(at + 0x3C) >> 16,
                PlayCount = r.U32(at + 0x50),
                LastPlayed = ItunesDbWriter.FromMacTime(r.U32(at + 0x58)),
                DiscNumber = r.U32(at + 0x5C),
                DiscCount = r.U32(at + 0x60),
                Added = ItunesDbWriter.FromMacTime(r.U32(at + 0x68)),
                SkipCount = header >= 0xA0 ? r.U32(at + 0x9C) : 0,
                LastSkipped = header >= 0xA4 ? ItunesDbWriter.FromMacTime(r.U32(at + 0xA0)) : null,
                Pregap = header >= 0xC0 ? r.U32(at + 0xB8) : 0,
                SampleCount = header >= 0xC4 ? r.U64(at + 0xBC) : 0,
                Postgap = header >= 0xCC ? r.U32(at + 0xC8) : 0,
                GaplessData = header >= 0xFC ? r.U32(at + 0xF8) : 0,
                GaplessTrack = header >= 0x102 && r.U16(at + 0x100) != 0,
                GaplessAlbum = header >= 0x104 && r.U16(at + 0x102) != 0,
                ArtworkId = header >= 0x164 && r.U8(at + 0xA4) == 1 ? r.U32(at + 0x160) : 0,
                ArtworkSize = r.U8(at + 0xA4) == 1 ? r.U32(at + 0x80) : 0,
            };
            tracks.Add(new ItdbReadTrack(r.U32(at + 0x10), track, description, marker));
            at = next;
        }
    }

    private static void ReadPlaylists(Reader r, int at, uint count, int end, List<ItdbReadPlaylist> playlists)
    {
        for (var i = 0; i < count; i++)
        {
            r.Expect(at, "mhyp");
            var header = r.U32(at + 4);
            var total = r.U32(at + 8);
            var mhods = r.U32(at + 12);
            var entries = r.U32(at + 16);
            var next = r.End(at, total, end);
            var isMaster = r.U8(at + 20) != 0;
            var id = r.U64(at + 28);
            var cursor = at + (int)header;
            string? name = null;
            var sortIndexes = 0;
            for (var m = 0; m < mhods; m++)
            {
                r.Expect(cursor, "mhod");
                var mhodTotal = r.U32(cursor + 8);
                var type = r.U32(cursor + 12);
                var mhodEnd = r.End(cursor, mhodTotal, next);
                if (type == 1)
                {
                    name = ReadString(r, cursor, mhodEnd);
                }
                else if (type == 52)
                {
                    sortIndexes++;
                }

                cursor = mhodEnd;
            }

            var trackIds = new List<uint>((int)Math.Min(entries, 100_000));
            for (var e = 0; e < entries; e++)
            {
                r.Expect(cursor, "mhip");
                var mhipTotal = r.U32(cursor + 8);
                trackIds.Add(r.U32(cursor + 24));
                cursor = r.End(cursor, mhipTotal, next);
            }

            playlists.Add(new ItdbReadPlaylist(id, name ?? string.Empty, isMaster, trackIds, sortIndexes));
            at = next;
        }
    }

    private static Dictionary<uint, string> ReadStrings(Reader r, int at, uint count, int end)
    {
        var strings = new Dictionary<uint, string>();
        for (var m = 0; m < count; m++)
        {
            r.Expect(at, "mhod");
            var total = r.U32(at + 8);
            var type = r.U32(at + 12);
            var next = r.End(at, total, end);
            if (type is (>= 1 and <= 14) or (>= 18 and <= 31) or (>= 200 and <= 202) or 300)
            {
                strings.TryAdd(type, ReadString(r, at, next));
            }

            at = next;
        }

        return strings;
    }

    private static string ReadString(Reader r, int at, int end)
    {
        var encoding = r.U32(at + 24);
        var length = (int)r.U32(at + 28);
        if (at + 40 + length > end)
        {
            throw new ItunesDbFormatException("A string runs past its record.");
        }

        var bytes = r.Slice(at + 40, length);
        return encoding == 2 ? Encoding.UTF8.GetString(bytes) : Encoding.Unicode.GetString(bytes);
    }

    private static ItdbFileKind KindOf(string? description, string path) => description switch
    {
        "MPEG audio file" => ItdbFileKind.Mp3,
        "Apple Lossless audio file" => ItdbFileKind.AppleLossless,
        "WAV audio file" => ItdbFileKind.Wav,
        "AIFF audio file" => ItdbFileKind.Aiff,
        "AAC audio file" => ItdbFileKind.Aac,
        _ => ItdbFileKinds.FromExtension(Path.GetExtension(path.Replace(':', '/'))) ?? ItdbFileKind.Aac,
    };

    private readonly ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public void Expect(int at, string tag)
        {
            if (at < 0 || at + 12 > _data.Length || !_data.Slice(at, 4).SequenceEqual(Encoding.ASCII.GetBytes(tag)))
            {
                throw new ItunesDbFormatException($"Expected {tag} at {at}.");
            }
        }

        /// <summary>The end of a record starting at <paramref name="at"/>, checked against its parent's end.</summary>
        public int End(int at, uint total, int parentEnd = int.MaxValue)
        {
            var end = (long)at + total;
            if (total < 12 || end > Math.Min(_data.Length, parentEnd))
            {
                throw new ItunesDbFormatException($"A record at {at} claims {total} bytes, past its end.");
            }

            return (int)end;
        }

        public byte U8(int at) => Check(at, 1)[0];

        public ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Check(at, 2));

        public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Check(at, 4));

        public ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(Check(at, 8));

        public ReadOnlySpan<byte> Slice(int at, int length) => Check(at, length);

        private ReadOnlySpan<byte> Check(int at, int length)
        {
            if (at < 0 || length < 0 || (long)at + length > _data.Length)
            {
                throw new ItunesDbFormatException($"Read of {length} bytes at {at} is past the end.");
            }

            return _data.Slice(at, length);
        }
    }
}
