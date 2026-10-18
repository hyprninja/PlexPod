using System.Buffers.Binary;
using System.Text;

namespace PlexPod.Core.ItunesDb;

/// <summary>A cover thumbnail size the firmware reads, and the <c>.ithmb</c> files that hold it.</summary>
public sealed record ArtworkFormat(int FormatId, int Width, int Height)
{
    /// <summary>The iPod Video (5G/5.5G) cover sizes (libgpod <c>ipod_video_cover_art_info</c>), both RGB565 little-endian.</summary>
    public static readonly IReadOnlyList<ArtworkFormat> IpodVideo = [new(1028, 100, 100), new(1029, 200, 200)];

    public int ImageBytes => Width * Height * 2;

    /// <summary><c>F1028_1.ithmb</c>, in <c>iPod_Control/Artwork</c>.</summary>
    public string FileName(int index) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"F{FormatId}_{index}.ithmb");
}

/// <summary>One thumbnail of a track's cover: where it sits in its <c>.ithmb</c> file and how it's padded.</summary>
public sealed record ArtworkThumb(int FormatId, string FileName, uint Offset, uint Size, ushort Width, ushort Height, ushort HorizontalPadding, ushort VerticalPadding);

/// <summary>One track's cover (an <c>mhii</c>): linked to the track by its database id.</summary>
public sealed record ArtworkEntry(uint ImageId, ulong SongDbid, uint SourceSize, IReadOnlyList<ArtworkThumb> Thumbs);

/// <summary>
/// Writes and reads <c>iPod_Control/Artwork/ArtworkDB</c> for the iPod Video's stock firmware, after
/// libgpod's <c>db-artwork-writer.c</c>: <c>mhfd</c> → three <c>mhsd</c> sections (images, empty
/// albums, the file list), each image an <c>mhii</c> with one thumbnail container per size, headers
/// padded to the sizes iTunes uses. Pixels live in the <c>.ithmb</c> files.
/// </summary>
public static class ArtworkDb
{
    /// <summary>Image ids start here (libgpod and iTunes).</summary>
    public const uint FirstImageId = 0x64;

    public static byte[] Write(IReadOnlyList<ArtworkEntry> entries, IReadOnlyList<ArtworkFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(formats);
        var w = new Buffer();
        var mhfd = w.Header("mhfd", 0x84);
        w.Patch32(mhfd + 12, 0);
        w.Patch32(mhfd + 16, 2);                                       // 2 since iTunes 4.9 (1 makes iTunes 7 discard it)
        w.Patch32(mhfd + 20, 3);                                       // sections
        w.Patch32(mhfd + 28, entries.Count == 0 ? 0 : entries.Max(e => e.ImageId)); // highest id, as libgpod writes it
        w.Patch8(mhfd + 48, 2);                                        // unknown flag, always 2

        // 1: images
        var section = Section(w, 1);
        var mhli = w.Header("mhli", 0x5c);
        w.Patch32(mhli + 8, (uint)entries.Count);
        foreach (var entry in entries)
        {
            var mhii = w.Header("mhii", 0x98);
            w.Patch32(mhii + 12, (uint)entry.Thumbs.Count);
            w.Patch32(mhii + 16, entry.ImageId);
            w.Patch64(mhii + 20, entry.SongDbid);
            w.Patch32(mhii + 48, entry.SourceSize);
            foreach (var thumb in entry.Thumbs)
            {
                var container = w.Position;
                w.Tag("mhod");
                w.U32(24);
                w.U32(0);                                              // total, fixed below
                w.U16(2);                                              // thumbnail container
                w.U16(0);
                w.Zeros(8);

                var mhni = w.Header("mhni", 0x4c);
                w.Patch32(mhni + 12, 1);
                w.Patch32(mhni + 16, (uint)thumb.FormatId);
                w.Patch32(mhni + 20, thumb.Offset);
                w.Patch32(mhni + 24, thumb.Size);
                w.Patch16(mhni + 28, thumb.VerticalPadding);
                w.Patch16(mhni + 30, thumb.HorizontalPadding);
                w.Patch16(mhni + 32, thumb.Height);
                w.Patch16(mhni + 34, thumb.Width);
                WriteFileName(w, ":" + thumb.FileName);
                w.FixTotal(mhni);
                w.FixTotal(container);
            }

            w.FixTotal(mhii);
        }

        w.FixTotal(section);

        // 2: albums (photos only; empty for music)
        section = Section(w, 2);
        w.Header("mhla", 0x5c);
        w.FixTotal(section);

        // 3: the thumbnail files, one per size
        section = Section(w, 3);
        var mhlf = w.Header("mhlf", 0x5c);
        w.Patch32(mhlf + 8, (uint)formats.Count);
        foreach (var format in formats)
        {
            var mhif = w.Header("mhif", 0x7c);
            w.Patch32(mhif + 8, 0x7c);
            w.Patch32(mhif + 16, (uint)format.FormatId);
            w.Patch32(mhif + 20, (uint)format.ImageBytes);
        }

        w.FixTotal(section);
        w.FixTotal(mhfd);
        return w.ToArray();
    }

    /// <summary>Reads the image list back (for checking a database before it goes on the iPod).</summary>
    public static IReadOnlyList<ArtworkEntry> Read(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        r.Expect(0, "mhfd");
        if (r.U32(8) != data.Length)
        {
            throw new ItunesDbFormatException("ArtworkDB size doesn't match its header.");
        }

        var entries = new List<ArtworkEntry>();
        var at = (int)r.U32(4);
        var sections = r.U32(20);
        for (var s = 0; s < sections; s++)
        {
            r.Expect(at, "mhsd");
            var end = r.End(at, r.U32(at + 8));
            if (r.U16(at + 12) == 1)
            {
                var list = at + (int)r.U32(at + 4);
                r.Expect(list, "mhli");
                var count = r.U32(list + 8);
                var cursor = list + (int)r.U32(list + 4);
                for (var i = 0; i < count; i++)
                {
                    r.Expect(cursor, "mhii");
                    var next = r.End(cursor, r.U32(cursor + 8), end);
                    var children = r.U32(cursor + 12);
                    var thumbs = new List<ArtworkThumb>();
                    var c = cursor + (int)r.U32(cursor + 4);
                    for (var t = 0; t < children; t++)
                    {
                        r.Expect(c, "mhod");
                        var containerEnd = r.End(c, r.U32(c + 8), next);
                        var mhni = c + (int)r.U32(c + 4);
                        r.Expect(mhni, "mhni");
                        var nameMhod = mhni + (int)r.U32(mhni + 4);
                        r.Expect(nameMhod, "mhod");
                        var nameLength = (int)r.U32(nameMhod + 24);
                        var name = r.Utf16(nameMhod + 36, nameLength);
                        thumbs.Add(new ArtworkThumb(
                            (int)r.U32(mhni + 16), name.TrimStart(':'), r.U32(mhni + 20), r.U32(mhni + 24),
                            r.U16(mhni + 34), r.U16(mhni + 32), r.U16(mhni + 30), r.U16(mhni + 28)));
                        c = containerEnd;
                    }

                    entries.Add(new ArtworkEntry(r.U32(cursor + 16), r.U64(cursor + 20), r.U32(cursor + 48), thumbs));
                    cursor = next;
                }
            }

            at = end;
        }

        return entries;
    }

    private static long Section(Buffer w, ushort index)
    {
        var at = w.Header("mhsd", 0x60);
        w.Patch16(at + 12, index);
        return at;
    }

    /// <summary>mhod type 3: the <c>.ithmb</c> file name in UTF-16, padded to 4 bytes (libgpod <c>write_mhod_type_3</c>).</summary>
    private static void WriteFileName(Buffer w, string name)
    {
        var utf16 = Encoding.Unicode.GetBytes(name);
        const int header = 36;
        var padding = (4 - ((header + utf16.Length) % 4)) % 4;
        w.Tag("mhod");
        w.U32(24);
        w.U32((uint)(header + utf16.Length + padding));
        w.U16(3);
        w.U8(0);
        w.U8((byte)padding);
        w.Zeros(8);
        w.U32((uint)utf16.Length);
        w.U8(2);                                                       // UTF-16LE
        w.U8(0);
        w.U16(0);
        w.U32(0);
        w.Bytes(utf16);
        w.Zeros(padding);
    }

    private sealed class Buffer
    {
        private readonly MemoryStream _s = new();
        private readonly byte[] _b = new byte[8];

        public long Position => _s.Position;

        public byte[] ToArray() => _s.ToArray();

        /// <summary>A zero-filled header of the given (padded) size, with its tag and length; returns where it starts.</summary>
        public long Header(string tag, int size)
        {
            var at = _s.Position;
            Tag(tag);
            U32((uint)size);
            Zeros(size - 8);
            return at;
        }

        public void Tag(string tag) => _s.Write(Encoding.ASCII.GetBytes(tag));

        public void U8(byte v) => _s.WriteByte(v);

        public void U16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_b, v);
            _s.Write(_b, 0, 2);
        }

        public void U32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_b, v);
            _s.Write(_b, 0, 4);
        }

        public void Zeros(int n) => _s.Write(new byte[n]);

        public void Bytes(byte[] bytes) => _s.Write(bytes);

        public void FixTotal(long at) => Patch32(at + 8, (uint)(_s.Position - at));

        public void Patch8(long at, byte v) => Patch(at, () => U8(v));

        public void Patch16(long at, ushort v) => Patch(at, () => U16(v));

        public void Patch32(long at, uint v) => Patch(at, () => U32(v));

        public void Patch64(long at, ulong v) => Patch(at, () =>
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_b, v);
            _s.Write(_b, 0, 8);
        });

        private void Patch(long at, Action write)
        {
            var here = _s.Position;
            _s.Position = at;
            write();
            _s.Position = here;
        }
    }

    private readonly ref struct SpanReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _d = data;

        public void Expect(int at, string tag)
        {
            if (at < 0 || at + 12 > _d.Length || !_d.Slice(at, 4).SequenceEqual(Encoding.ASCII.GetBytes(tag)))
            {
                throw new ItunesDbFormatException($"Expected {tag} at {at} in the ArtworkDB.");
            }
        }

        public int End(int at, uint total, int parentEnd = int.MaxValue)
        {
            var end = (long)at + total;
            if (total < 12 || end > Math.Min(_d.Length, parentEnd))
            {
                throw new ItunesDbFormatException($"An ArtworkDB record at {at} runs past its end.");
            }

            return (int)end;
        }

        public ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(Slice(at, 2));

        public uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(at, 4));

        public ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(at, 8));

        public string Utf16(int at, int length) => Encoding.Unicode.GetString(Slice(at, length));

        private ReadOnlySpan<byte> Slice(int at, int length)
        {
            if (at < 0 || length < 0 || (long)at + length > _d.Length)
            {
                throw new ItunesDbFormatException("Read past the end of the ArtworkDB.");
            }

            return _d.Slice(at, length);
        }
    }
}
