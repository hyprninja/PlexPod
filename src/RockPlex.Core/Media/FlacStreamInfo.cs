using System.Buffers.Binary;

namespace RockPlex.Core.Media;

/// <summary>
/// The STREAMINFO block of a FLAC file: what the audio really is, read from the downloaded file
/// rather than trusted from Plex metadata.
/// </summary>
public sealed record FlacStreamInfo(
    int MinBlockSize,
    int MaxBlockSize,
    int SampleRateHz,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    string AudioMd5)
{
    /// <summary>Bytes needed after the <c>fLaC</c> marker: one block header plus the 34-byte STREAMINFO.</summary>
    public const int StreamInfoLength = 4 + 34;

    /// <summary>
    /// Length of an ID3v2 tag glued onto the front of the file (common in messy FLACs), or 0.
    /// Needs the first 10 bytes.
    /// </summary>
    public static long LeadingId3Length(ReadOnlySpan<byte> first10)
    {
        if (first10.Length < 10 || first10[0] != 'I' || first10[1] != 'D' || first10[2] != '3')
        {
            return 0;
        }

        // Syncsafe size (7 bits per byte), excluding the 10-byte header; +10 more if a footer is present.
        var size = (first10[6] & 0x7F) << 21 | (first10[7] & 0x7F) << 14 | (first10[8] & 0x7F) << 7 | (first10[9] & 0x7F);
        var hasFooter = (first10[5] & 0x10) != 0;
        return 10L + size + (hasFooter ? 10 : 0);
    }

    /// <summary>
    /// Parses <c>fLaC</c> + STREAMINFO from the start of <paramref name="data"/> (after any leading ID3v2,
    /// which callers skip with <see cref="LeadingId3Length"/>).
    /// </summary>
    /// <exception cref="FormatException">Not FLAC, truncated, or STREAMINFO is not the first block.</exception>
    public static FlacStreamInfo Parse(ReadOnlySpan<byte> data)
    {
        var id3 = LeadingId3Length(data);
        if (id3 > 0)
        {
            if (id3 > data.Length)
            {
                throw new FormatException("FLAC header truncated inside a leading ID3v2 tag.");
            }

            data = data[(int)id3..];
        }

        if (data.Length < 4 || data[0] != 'f' || data[1] != 'L' || data[2] != 'a' || data[3] != 'C')
        {
            throw new FormatException("Not a FLAC file (no fLaC marker).");
        }

        if (data.Length < 4 + StreamInfoLength)
        {
            throw new FormatException("FLAC header truncated before the end of STREAMINFO.");
        }

        var header = data[4..8];
        var blockType = header[0] & 0x7F;
        var blockLength = header[1] << 16 | header[2] << 8 | header[3];
        if (blockType != 0 || blockLength < 34)
        {
            throw new FormatException("FLAC STREAMINFO is not the first metadata block.");
        }

        var s = data[8..42];
        var minBlock = BinaryPrimitives.ReadUInt16BigEndian(s);
        var maxBlock = BinaryPrimitives.ReadUInt16BigEndian(s[2..]);

        // Bytes 10..17: sample rate (20 bits), channels - 1 (3), bits per sample - 1 (5), total samples (36).
        var packed = BinaryPrimitives.ReadUInt64BigEndian(s[10..]);
        var sampleRate = (int)(packed >> 44);
        var channels = (int)((packed >> 41) & 0x7) + 1;
        var bits = (int)((packed >> 36) & 0x1F) + 1;
        var total = (long)(packed & 0xF_FFFF_FFFF);
        var md5 = Convert.ToHexStringLower(s[18..34]);

        if (sampleRate == 0)
        {
            throw new FormatException("FLAC STREAMINFO has a zero sample rate.");
        }

        return new FlacStreamInfo(minBlock, maxBlock, sampleRate, channels, bits, total, md5);
    }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)TotalSamples / SampleRateHz);

    /// <summary>All zeros means the encoder didn't compute one, so it can't be used for verification.</summary>
    public bool HasAudioMd5 => AudioMd5.Any(c => c != '0');
}
