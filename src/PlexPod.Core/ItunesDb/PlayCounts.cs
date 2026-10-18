using System.Buffers.Binary;
using System.Text;

namespace PlexPod.Core.ItunesDb;

/// <summary>
/// One entry of the firmware's <c>iTunes/Play Counts</c>: what happened to the track at the same
/// position in the <c>iTunesDB</c> since a computer last consumed the file. <see cref="Rating"/> is the
/// track's current rating (stars × 20), or null when the entry is too short to hold one.
/// </summary>
public sealed record PlayCountEntry(uint Plays, DateTime? LastPlayed, uint BookmarkMs, byte? Rating, uint Skips, DateTime? LastSkipped);

/// <summary>Parses <c>Play Counts</c> (libgpod <c>playcounts_read</c>): an <c>mhdp</c> header, then fixed-size entries.</summary>
public static class PlayCountsReader
{
    public static IReadOnlyList<PlayCountEntry> Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual("mhdp"u8))
        {
            throw new ItunesDbFormatException("Not a Play Counts file (no mhdp header).");
        }

        var header = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (header < 0x60 || entryLength < 0x0C || header + (long)entryLength * count > data.Length)
        {
            throw new ItunesDbFormatException($"Play Counts header ({header}, {entryLength} × {count}) doesn't fit its {data.Length} bytes.");
        }

        var entries = new List<PlayCountEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice((int)(header + (i * entryLength)), (int)entryLength);
            var plays = BinaryPrimitives.ReadUInt32LittleEndian(e);
            var lastPlayed = BinaryPrimitives.ReadUInt32LittleEndian(e[4..]);
            var bookmark = BinaryPrimitives.ReadUInt32LittleEndian(e[8..]);
            byte? rating = entryLength >= 0x10 ? (byte)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), 100) : null;
            var skips = entryLength >= 0x1C ? BinaryPrimitives.ReadUInt32LittleEndian(e[20..]) : 0;
            var lastSkipped = entryLength >= 0x1C ? BinaryPrimitives.ReadUInt32LittleEndian(e[24..]) : 0;
            entries.Add(new PlayCountEntry(
                plays,
                ItunesDbWriter.FromMacTime(lastPlayed),
                bookmark,
                rating,
                skips,
                ItunesDbWriter.FromMacTime(lastSkipped)));
        }

        return entries;
    }

    /// <summary>Builds a file in the firmware's layout (0x60 header, 0x1C entries). For tests and fixtures.</summary>
    public static byte[] Write(IReadOnlyList<PlayCountEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var data = new byte[0x60 + (entries.Count * 0x1C)];
        Encoding.ASCII.GetBytes("mhdp").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x1C);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            var e = data.AsSpan(0x60 + (i * 0x1C), 0x1C);
            var p = entries[i];
            BinaryPrimitives.WriteUInt32LittleEndian(e, p.Plays);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], ItunesDbWriter.ToMacTime(p.LastPlayed));
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], p.BookmarkMs);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], p.Rating ?? 0);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], p.Skips);
            BinaryPrimitives.WriteUInt32LittleEndian(e[24..], ItunesDbWriter.ToMacTime(p.LastSkipped));
        }

        return data;
    }
}
