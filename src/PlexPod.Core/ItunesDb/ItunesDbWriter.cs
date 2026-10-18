using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PlexPod.Core.ItunesDb;

/// <summary>
/// Writes a complete <c>iTunesDB</c> for the iPod Video (5G/5.5G) stock firmware: a full rebuild,
/// never an edit. The layout copies libgpod's writer (<c>itdb_itunesdb.c</c>, iTunesDB version 0x30,
/// no checksum), which gtkpod has used on these iPods for years: the same sections in the same order
/// (tracks, podcasts view, playlists, albums, artists, two empty lists, the type-5 list), the master
/// playlist with its five sort indexes and jump tables, and the same fixed values for fields whose
/// meaning is unknown. Deterministic: the same library gives the same bytes.
/// </summary>
public static class ItunesDbWriter
{
    /// <summary>iTunes 9.2; what libgpod writes. Older firmware accepts it.</summary>
    public const uint Version = 0x30;

    /// <summary>Track ids start here, as in libgpod (and iTunes).</summary>
    public const uint FirstTrackId = 52;

    private const uint MhbdHeader = 244;
    private const uint MhsdHeader = 96;
    private const uint ListHeader = 92;
    private const uint MhitHeader = 0x248;
    private const uint MhypHeader = 108;
    private const uint MhipHeader = 76;
    private const uint MhiaHeader = 88;
    private const uint MhiiHeader = 80;

    // Sort indexes in the master playlist (mhod 52/53).
    private const uint SortTitle = 0x03;
    private const uint SortAlbum = 0x04;
    private const uint SortArtist = 0x05;
    private const uint SortGenre = 0x07;
    private const uint SortComposer = 0x12;

    public static byte[] Write(ItdbLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        Validate(library);

        var ids = Ids.Assign(library.Tracks);
        var w = new LeWriter();
        var mhbd = w.Position;
        WriteMhbd(w, library, children: 8);

        // 1: tracks
        var section = BeginSection(w, 1);
        WriteHeaderOnlyList(w, "mhlt", (uint)library.Tracks.Count);
        for (var i = 0; i < library.Tracks.Count; i++)
        {
            WriteTrack(w, library.Tracks[i], ids, i);
        }

        w.FixTotal(section);

        // 3 (the podcast view, which with no podcasts lists the same playlists), then 2.
        WritePlaylistSection(w, 3, library, ids);
        WritePlaylistSection(w, 2, library, ids);

        // 4: albums, 8: artists, 6 and 10: empty lists, 5: no special playlists.
        section = BeginSection(w, 4);
        WriteHeaderOnlyList(w, "mhla", (uint)ids.Albums.Count);
        foreach (var album in ids.Albums)
        {
            WriteMhia(w, album);
        }

        w.FixTotal(section);

        section = BeginSection(w, 8);
        WriteHeaderOnlyList(w, "mhli", (uint)ids.Artists.Count);
        foreach (var artist in ids.Artists)
        {
            WriteMhii(w, artist);
        }

        w.FixTotal(section);

        foreach (var empty in new uint[] { 6, 10 })
        {
            section = BeginSection(w, empty);
            WriteHeaderOnlyList(w, "mhlt", 0);
            w.FixTotal(section);
        }

        section = BeginSection(w, 5);
        WriteHeaderOnlyList(w, "mhlp", 0);
        w.FixTotal(section);

        w.FixTotal(mhbd);
        return w.ToArray();
    }

    /// <summary>Wall-clock time to the firmware's seconds since 1904 (0 means "never").</summary>
    public static uint ToMacTime(DateTime? time) =>
        time is { } t && t > MacEpoch ? (uint)Math.Min(uint.MaxValue, (t - MacEpoch).TotalSeconds) : 0;

    public static DateTime? FromMacTime(uint seconds) => seconds == 0 ? null : MacEpoch.AddSeconds(seconds);

    public static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>The 4-character code the firmware expects, from the file's extension (libgpod's <c>itdb_cp_finalize</c>).</summary>
    public static uint FileTypeMarker(string ipodPath)
    {
        var dot = ipodPath.LastIndexOf('.');
        var suffix = dot < 0 ? string.Empty : ipodPath[(dot + 1)..].ToUpperInvariant();
        uint marker = 0;
        for (var i = 0; i < 4; i++)
        {
            marker = (marker << 8) | (i < suffix.Length && suffix[i] < 128 ? suffix[i] : ' ');
        }

        return marker;
    }

    private static void Validate(ItdbLibrary library)
    {
        var dbids = new HashSet<ulong>();
        foreach (var track in library.Tracks)
        {
            if (!dbids.Add(track.Dbid))
            {
                throw new ArgumentException($"Two tracks share the id {track.Dbid}.", nameof(library));
            }

            if (track.IpodPath.Length < 2 || track.IpodPath[0] != ':')
            {
                throw new ArgumentException($"Not an iPod colon path: {track.IpodPath}", nameof(library));
            }
        }

        foreach (var playlist in library.Playlists)
        {
            foreach (var entry in playlist.Entries.Where(e => !dbids.Contains(e)).Take(1))
            {
                throw new ArgumentException($"Playlist \"{playlist.Name}\" names a track that isn't in the database ({entry}).", nameof(library));
            }
        }
    }

    // ---------------------------------------------------------------- headers

    private static void WriteMhbd(LeWriter w, ItdbLibrary library, uint children)
    {
        var start = w.Position;
        w.Tag("mhbd");
        w.U32(MhbdHeader);
        w.U32(0);           // total size, fixed later
        w.U32(1);           // 1 on the iPod Color and Classic
        w.U32(Version);
        w.U32(children);
        w.U64(library.DatabaseId);
        w.U16(1);           // 0x20 platform: 1 = Mac (libgpod's default for a new database)
        w.U16(0);           // 0x22
        w.U64(0);           // 0x24
        w.U32(0);
        w.U16(0);           // 0x30 hashing scheme: none on this iPod
        w.Zeros(20);
        w.U16(0x656e);      // 0x46 language "en"
        w.U64(library.LibraryId);
        w.U32(0);           // 0x50
        w.U32(0);           // 0x54
        w.Zeros(20);        // hash58 (unused here)
        w.U32(0);           // time zone offset
        w.U16(0);           // 0x70 checksum type
        w.U16(0);
        w.Zeros(44);        // hash72
        w.U16(0);           // 0xa0 audio language
        w.U16(0);           // subtitle language
        w.U16(0);
        w.U16(0);
        w.U16(0);
        w.U8(0);
        w.U8(0);            // 0xab hashAB
        w.Zeros(56);
        w.Zeros(16);
        w.CheckHeader(start, MhbdHeader);
    }

    private static long BeginSection(LeWriter w, uint type)
    {
        var start = w.Position;
        w.Tag("mhsd");
        w.U32(MhsdHeader);
        w.U32(0);   // total size, fixed later
        w.U32(type);
        w.Zeros(80);
        w.CheckHeader(start, MhsdHeader);
        return start;
    }

    /// <summary>mhlt / mhlp / mhla / mhli: a 92-byte header with a count and no total size.</summary>
    private static long WriteHeaderOnlyList(LeWriter w, string tag, uint count)
    {
        var start = w.Position;
        w.Tag(tag);
        w.U32(ListHeader);
        w.U32(count);
        w.Zeros(80);
        w.CheckHeader(start, ListHeader);
        return start;
    }

    // ---------------------------------------------------------------- tracks

    private static void WriteTrack(LeWriter w, ItdbTrack t, Ids ids, int index)
    {
        var start = w.Position;
        var (type1, type2, unk126, unk144) = t.Kind switch
        {
            ItdbFileKind.Mp3 => ((byte)1, (byte)1, (ushort)0xffff, (ushort)0x000c),
            ItdbFileKind.Aac or ItdbFileKind.AppleLossless => ((byte)0, (byte)0, (ushort)0xffff, (ushort)0x0033),
            _ => ((byte)0, (byte)0, (ushort)0, (ushort)0),
        };

        w.Tag("mhit");
        w.U32(MhitHeader);
        w.U32(0);                               // total size, fixed later
        w.U32(0);                               // mhod count, fixed later
        w.U32(FirstTrackId + (uint)index);      // 0x10 id
        w.U32(1);                               // visible
        w.U32(FileTypeMarker(t.IpodPath));
        w.U8(type1);
        w.U8(type2);
        w.U8(t.Compilation ? (byte)1 : (byte)0);
        w.U8(Math.Min(t.Rating, (byte)100));
        w.U32(ToMacTime(t.Modified));           // 0x20
        w.U32(t.SizeBytes);
        w.U32(t.LengthMs);
        w.U32(t.TrackNumber);
        w.U32(t.TrackCount);                    // 0x30
        w.U32(t.Year);
        w.U32(t.Bitrate);
        w.U32(t.SampleRate << 16);
        w.U32(0);                               // 0x40 volume
        w.U32(0);                               // start time
        w.U32(0);                               // stop time
        w.U32(0);                               // Sound Check
        w.U32(t.PlayCount);                     // 0x50
        w.U32(t.PlayCount);                     // play count 2
        w.U32(ToMacTime(t.LastPlayed));
        w.U32(t.DiscNumber);
        w.U32(t.DiscCount);                     // 0x60
        w.U32(0);                               // DRM user id
        w.U32(ToMacTime(t.Added));
        w.U32(0);                               // bookmark
        w.U64(t.Dbid);                          // 0x70
        w.U8(0);                                // checked
        w.U8(0);                                // app rating
        w.U16(0);                               // BPM
        w.U16(0);                               // artwork count
        w.U16(unk126);
        w.U32(0);                               // 0x80 artwork size
        w.U32(0);
        w.F32(t.SampleRate);
        w.U32(0);                               // release date
        w.U16(unk144);                          // 0x90
        w.U16(0);                               // explicit
        w.U32(0);
        w.U32(0);
        w.U32(t.SkipCount);
        w.U32(ToMacTime(t.LastSkipped));        // 0xA0
        w.U8(2);                                // has artwork: 2 = no
        w.U8(0);                                // skip when shuffling
        w.U8(0);                                // remember position
        w.U8(0);
        w.U64(t.Dbid);                          // dbid2
        w.U8(0);                                // 0xB0 lyrics
        w.U8(0);                                // movie
        w.U8(1);                                // mark unplayed: 1 = no bullet
        w.U8(0);
        w.U32(0);
        w.U32(t.Pregap);
        w.U64(t.SampleCount);
        w.U32(0);
        w.U32(t.Postgap);
        w.U32(0);
        w.U32(1);                               // 0xD0 media type: audio
        w.U32(0);                               // season
        w.U32(0);                               // episode
        w.U32(0);
        w.Zeros(16);                            // 0xE0
        w.U32(0);                               // 0xF0
        w.U32(0);
        w.U32(t.GaplessData);
        w.U32(0);
        w.U16(t.GaplessTrack ? (ushort)1 : (ushort)0); // 0x100
        w.U16(t.GaplessAlbum ? (ushort)1 : (ushort)0);
        w.Zeros(28);
        w.U32(ids.AlbumIdOf[index]);            // 0x120
        w.U64(0);                               // same as mhbd 0x24
        w.U32(t.SizeBytes);
        w.U32(0);                               // 0x130
        w.U64(0x808080808080);
        w.U32(0);
        w.Zeros(8);                             // 0x140
        w.U32(0);
        w.Zeros(20);
        w.U32(0);                               // 0x160 artwork link (Phase 6)
        w.U32(0);
        w.U32(1);
        w.U32(0);
        w.Zeros(112);                           // 0x170
        w.U32(ids.ArtistIdOf[index]);           // 0x1E0
        w.Zeros(16);
        w.U32(ids.ComposerIdOf[index]);         // 0x1F4
        w.Zeros(80);
        w.CheckHeader(start, MhitHeader);

        uint mhods = 0;
        void Text(uint type, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                WriteStringMhod(w, type, value);
                mhods++;
            }
        }

        // libgpod's order.
        Text(1, t.Title);
        Text(4, t.Artist);
        Text(3, t.Album);
        Text(6, ItdbFileKinds.Description(t.Kind));
        Text(8, t.Comment);
        Text(2, t.IpodPath);
        Text(5, t.Genre);
        Text(12, t.Composer);
        Text(13, t.Grouping);
        Text(22, t.AlbumArtist);
        Text(23, t.SortArtist);
        Text(29, t.SortAlbumArtist);

        w.FixTotal(start);
        w.PatchU32(start + 12, mhods);
    }

    private static void WriteStringMhod(LeWriter w, uint type, string value)
    {
        var utf16 = Encoding.Unicode.GetBytes(value);
        w.Tag("mhod");
        w.U32(24);
        w.U32((uint)utf16.Length + 40);
        w.U32(type);
        w.Zeros(8);
        w.U32(1);                   // string encoding: UTF-16
        w.U32((uint)utf16.Length);
        w.U32(1);
        w.U32(0);
        w.Bytes(utf16);
    }

    // ---------------------------------------------------------------- playlists

    private static void WritePlaylistSection(LeWriter w, uint type, ItdbLibrary library, Ids ids)
    {
        var section = BeginSection(w, type);
        WriteHeaderOnlyList(w, "mhlp", (uint)(library.Playlists.Count + 1));
        var all = Enumerable.Range(0, library.Tracks.Count).ToList();
        WritePlaylist(w, library.MasterName, library.MasterId, master: true, all, library, ids);
        foreach (var playlist in library.Playlists)
        {
            var entries = playlist.Entries.Select(dbid => ids.IndexOf[dbid]).ToList();
            WritePlaylist(w, playlist.Name, playlist.Id, master: false, entries, library, ids);
        }

        w.FixTotal(section);
    }

    private static void WritePlaylist(LeWriter w, string name, ulong id, bool master, List<int> entries, ItdbLibrary library, Ids ids)
    {
        var start = w.Position;
        var indexes = master && entries.Count > 0;
        w.Tag("mhyp");
        w.U32(MhypHeader);
        w.U32(0);                                   // total size, fixed later
        w.U32(indexes ? 12u : 2u);                  // mhods: name, preferences (+ 5 sort indexes and 5 jump tables)
        w.U32((uint)entries.Count);
        w.U8(master ? (byte)1 : (byte)0);           // 1 = master (hidden)
        w.U8(0);
        w.U8(0);
        w.U8(0);
        w.U32(0);                                   // timestamp
        w.U64(id);
        w.U32(0);
        w.U16(1);                                   // string mhods
        w.U16(0);                                   // podcast flag
        w.U32(0);                                   // sort order
        w.Zeros(60);
        w.CheckHeader(start, MhypHeader);

        WriteStringMhod(w, 1, name);
        WritePlaylistPreferences(w);
        if (indexes)
        {
            var keys = library.Tracks.Select(SortKeys.For).ToList();
            foreach (var sort in new[] { SortTitle, SortArtist, SortAlbum, SortGenre, SortComposer })
            {
                WriteSortIndex(w, sort, keys);
            }
        }

        for (var position = 0; position < entries.Count; position++)
        {
            var mhip = w.Position;
            w.Tag("mhip");
            w.U32(MhipHeader);
            w.U32(0);                               // total size (with its mhod), fixed later
            w.U32(1);                               // children
            w.U32(0);                               // podcast group flag
            w.U32(0);                               // podcast group id
            w.U32(FirstTrackId + (uint)entries[position]);
            w.U32(0);                               // timestamp
            w.U32(0);                               // podcast group ref
            w.Zeros(40);
            w.CheckHeader(mhip, MhipHeader);

            w.Tag("mhod");
            w.U32(24);
            w.U32(44);
            w.U32(100);
            w.Zeros(8);
            w.U32((uint)position);
            w.Zeros(16);
            w.FixTotal(mhip);
        }

        w.FixTotal(start);
    }

    /// <summary>libgpod's fixed "long mhod 100": iTunes' column preferences, needed for iTunes to show the list.</summary>
    private static void WritePlaylistPreferences(LeWriter w)
    {
        var start = w.Position;
        w.Tag("mhod");
        w.U32(0x18);
        w.U32(0x288);
        w.U32(100);
        w.Zeros(24);
        foreach (var value in new uint[] { 0x010084, 0x05, 0x09, 0x03, 0x120001 })
        {
            w.U32(value);
        }

        foreach (var value in new uint[] { 0xc80002, 0x3c000d, 0x7d0004, 0x7d0003, 0x640008 })
        {
            w.Zeros(12);
            w.U32(value);
        }

        w.Zeros(12);
        foreach (var value in new uint[] { 0x640017, 0x500014, 0x7d0015 })
        {
            w.U32(value);
            w.U32(1);
            w.Zeros(8);
        }

        w.Zeros(456 - 8);
        if (w.Position - start != 0x288)
        {
            throw new InvalidOperationException($"Playlist preferences are {w.Position - start} bytes, expected 648.");
        }
    }

    /// <summary>mhod 52 (tracks sorted one way) and mhod 53 (where each first letter starts), as libgpod writes them.</summary>
    private static void WriteSortIndex(LeWriter w, uint sortType, List<SortKeys> keys)
    {
        var order = Enumerable.Range(0, keys.Count).ToList();
        order.Sort((a, b) => SortKeys.Compare(sortType, keys[a], keys[b]));

        w.Tag("mhod");
        w.U32(24);
        w.U32((uint)(4 * order.Count + 72));
        w.U32(52);
        w.Zeros(8);
        w.U32(sortType);
        w.U32((uint)order.Count);
        w.Zeros(40);
        var jumps = new List<(char Letter, uint Start, uint Count)>();
        for (var i = 0; i < order.Count; i++)
        {
            w.U32((uint)order[i]);
            var letter = keys[order[i]].Letter(sortType);
            if (jumps.Count == 0 || jumps[^1].Letter != letter)
            {
                jumps.Add((letter, (uint)i, 0));
            }

            jumps[^1] = jumps[^1] with { Count = jumps[^1].Count + 1 };
        }

        w.Tag("mhod");
        w.U32(24);
        w.U32((uint)(12 * jumps.Count + 40));
        w.U32(53);
        w.Zeros(8);
        w.U32(sortType);
        w.U32((uint)jumps.Count);
        w.Zeros(8);
        foreach (var (letter, startAt, count) in jumps)
        {
            w.U16(letter);
            w.U16(0);
            w.U32(startAt);
            w.U32(count);
        }
    }

    // ---------------------------------------------------------------- albums and artists

    private static void WriteMhia(LeWriter w, AlbumEntry album)
    {
        var start = w.Position;
        w.Tag("mhia");
        w.U32(MhiaHeader);
        w.U32(0);
        w.U32(0);           // mhod count, fixed later
        w.U32(album.Id);
        w.U64(StableId("album|" + album.Album + "|" + album.Artist));
        w.U32(2);
        w.Zeros(56);
        w.CheckHeader(start, MhiaHeader);
        uint mhods = 0;
        foreach (var (type, value) in new[] { (200u, album.Album), (201u, album.Artist), (202u, album.SortArtist) })
        {
            if (!string.IsNullOrEmpty(value))
            {
                WriteStringMhod(w, type, value);
                mhods++;
            }
        }

        w.FixTotal(start);
        w.PatchU32(start + 12, mhods);
    }

    private static void WriteMhii(LeWriter w, (uint Id, string Artist) artist)
    {
        var start = w.Position;
        w.Tag("mhii");
        w.U32(MhiiHeader);
        w.U32(0);
        w.U32(1);
        w.U32(artist.Id);
        w.U64(StableId("artist|" + artist.Artist));
        w.U32(2);
        w.Zeros(48);
        w.CheckHeader(start, MhiiHeader);
        WriteStringMhod(w, 300, artist.Artist);
        w.FixTotal(start);
    }

    /// <summary>A deterministic 64-bit id (libgpod uses random ones; these keep rebuilds byte-identical).</summary>
    public static ulong StableId(string key) =>
        BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private sealed record AlbumEntry(uint Id, string Album, string? Artist, string? SortArtist);

    /// <summary>Album, artist and composer numbering, as libgpod's <c>prepare_itdb_for_write</c> does it.</summary>
    private sealed class Ids
    {
        public List<AlbumEntry> Albums { get; } = [];

        public List<(uint Id, string Artist)> Artists { get; } = [];

        public uint[] AlbumIdOf { get; private init; } = [];

        public uint[] ArtistIdOf { get; private init; } = [];

        public uint[] ComposerIdOf { get; private init; } = [];

        public Dictionary<ulong, int> IndexOf { get; } = [];

        public static Ids Assign(IReadOnlyList<ItdbTrack> tracks)
        {
            var ids = new Ids
            {
                AlbumIdOf = new uint[tracks.Count],
                ArtistIdOf = new uint[tracks.Count],
                ComposerIdOf = new uint[tracks.Count],
            };
            var albums = new Dictionary<(string, string), uint>();
            var artists = new Dictionary<string, uint>(StringComparer.Ordinal);
            var composers = new Dictionary<string, uint>(StringComparer.Ordinal);
            for (var i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                ids.IndexOf[t.Dbid] = i;
                if (!string.IsNullOrEmpty(t.Album))
                {
                    // Same album = same name and the same album artist (or artist when there's none).
                    var key = (t.Album, t.AlbumArtist ?? t.Artist ?? string.Empty);
                    if (!albums.TryGetValue(key, out var id))
                    {
                        id = (uint)albums.Count + 1;
                        albums[key] = id;
                        ids.Albums.Add(new AlbumEntry(id, t.Album, t.AlbumArtist ?? t.Artist, t.SortAlbumArtist ?? t.SortArtist));
                    }

                    ids.AlbumIdOf[i] = id;
                }

                if (!string.IsNullOrEmpty(t.Artist))
                {
                    if (!artists.TryGetValue(t.Artist, out var id))
                    {
                        id = (uint)artists.Count + 1;
                        artists[t.Artist] = id;
                        ids.Artists.Add((id, t.Artist));
                    }

                    ids.ArtistIdOf[i] = id;
                }

                if (!string.IsNullOrEmpty(t.Composer))
                {
                    if (!composers.TryGetValue(t.Composer, out var id))
                    {
                        id = (uint)composers.Count + 1;
                        composers[t.Composer] = id;
                    }

                    ids.ComposerIdOf[i] = id;
                }
            }

            return ids;
        }
    }

    /// <summary>What the five sort indexes compare (libgpod's <c>mhod52_make_collate_keys</c>).</summary>
    private sealed record SortKeys(string Title, string Album, string Artist, string Genre, string Composer, uint Disc, uint Track)
    {
        private static readonly CompareInfo Collation = CultureInfo.InvariantCulture.CompareInfo;

        public static SortKeys For(ItdbTrack t) => new(
            t.Title,
            t.Album ?? string.Empty,
            t.SortArtist is { Length: > 0 } sort ? sort : WithoutThe(t.Artist ?? string.Empty),
            t.Genre ?? string.Empty,
            t.Composer ?? string.Empty,
            t.DiscNumber,
            t.TrackNumber);

        /// <summary>"The Beatles" sorts as "Beatles, The", as iTunes does.</summary>
        private static string WithoutThe(string artist) =>
            artist.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? artist[4..] + ", The" : artist;

        public char Letter(uint sortType) => JumpLetter(sortType switch
        {
            SortTitle => Title,
            SortAlbum => Album,
            SortArtist => Artist,
            SortGenre => Genre,
            _ => Composer,
        });

        public static int Compare(uint sortType, SortKeys a, SortKeys b)
        {
            int Text(string x, string y) => Collation.Compare(x, y, CompareOptions.IgnoreCase);
            int AlbumOrder() => FirstNonZero(Text(a.Album, b.Album), a.Disc.CompareTo(b.Disc), a.Track.CompareTo(b.Track), Text(a.Title, b.Title));
            return sortType switch
            {
                SortTitle => Text(a.Title, b.Title),
                SortAlbum => AlbumOrder(),
                SortArtist => FirstNonZero(Text(a.Artist, b.Artist), AlbumOrder()),
                SortGenre => FirstNonZero(Text(a.Genre, b.Genre), Text(a.Artist, b.Artist), AlbumOrder()),
                _ => FirstNonZero(Text(a.Composer, b.Composer), AlbumOrder()),
            };
        }

        private static int FirstNonZero(params int[] results) => results.FirstOrDefault(r => r != 0);

        /// <summary>The first letter or digit, upper-cased; '0' for digits and for strings with neither.</summary>
        private static char JumpLetter(string value)
        {
            foreach (var c in value)
            {
                if (char.IsLetter(c))
                {
                    return char.ToUpperInvariant(c);
                }

                if (char.IsDigit(c))
                {
                    return '0';
                }
            }

            return '0';
        }
    }

    /// <summary>Little-endian writer with back-patching of size fields.</summary>
    private sealed class LeWriter
    {
        private readonly MemoryStream _stream = new();
        private readonly byte[] _scratch = new byte[8];

        public long Position => _stream.Position;

        public byte[] ToArray() => _stream.ToArray();

        public void Tag(string tag) => _stream.Write(Encoding.ASCII.GetBytes(tag));

        public void U8(byte value) => _stream.WriteByte(value);

        public void U16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 2);
        }

        public void U16(char value) => U16((ushort)value);

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 4);
        }

        public void U64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 8);
        }

        public void F32(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_scratch, value);
            _stream.Write(_scratch, 0, 4);
        }

        public void Zeros(int count) => _stream.Write(new byte[count]);

        public void Bytes(byte[] bytes) => _stream.Write(bytes);

        /// <summary>Writes the record's total length (everything from <paramref name="start"/> to here) at offset 8.</summary>
        public void FixTotal(long start) => PatchU32(start + 8, (uint)(Position - start));

        public void PatchU32(long at, uint value)
        {
            var here = _stream.Position;
            _stream.Position = at;
            U32(value);
            _stream.Position = here;
        }

        public void CheckHeader(long start, uint expected)
        {
            if (Position - start != expected)
            {
                throw new InvalidOperationException($"Header written as {Position - start} bytes, expected {expected}.");
            }
        }
    }
}
