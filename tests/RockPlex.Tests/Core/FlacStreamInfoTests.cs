using RockPlex.Core.Media;

namespace RockPlex.Tests.Core;

public sealed class FlacStreamInfoTests
{
    /// <summary>Builds "fLaC" + a STREAMINFO block with the given values.</summary>
    internal static byte[] Header(int rate, int bits, int channels = 2, int minBlock = 4096, int maxBlock = 4096, long samples = 441_000, byte md5Fill = 0xAB, bool last = true)
    {
        var bytes = new List<byte> { (byte)'f', (byte)'L', (byte)'a', (byte)'C', (byte)(last ? 0x80 : 0x00), 0, 0, 34 };
        bytes.AddRange([(byte)(minBlock >> 8), (byte)minBlock, (byte)(maxBlock >> 8), (byte)maxBlock]);
        bytes.AddRange([0, 0, 0, 0, 0, 0]); // min/max frame size, unused
        var packed = ((ulong)rate << 44) | ((ulong)(channels - 1) << 41) | ((ulong)(bits - 1) << 36) | (ulong)samples;
        for (var shift = 56; shift >= 0; shift -= 8)
        {
            bytes.Add((byte)(packed >> shift));
        }

        bytes.AddRange(Enumerable.Repeat(md5Fill, 16));
        return [.. bytes];
    }

    [Fact]
    public void Parses_HiResStereo()
    {
        var info = FlacStreamInfo.Parse(Header(96_000, 24, samples: 96_000 * 60, maxBlock: 8192));
        Assert.Equal(96_000, info.SampleRateHz);
        Assert.Equal(24, info.BitsPerSample);
        Assert.Equal(2, info.Channels);
        Assert.Equal(4096, info.MinBlockSize);
        Assert.Equal(8192, info.MaxBlockSize);
        Assert.Equal(96_000 * 60, info.TotalSamples);
        Assert.Equal(TimeSpan.FromSeconds(60), info.Duration);
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", 16)), info.AudioMd5);
        Assert.True(info.HasAudioMd5);
    }

    [Fact]
    public void Parses_MonoAndMaxValues()
    {
        var info = FlacStreamInfo.Parse(Header(655_350, 32, channels: 1, samples: 0xF_FFFF_FFFF, md5Fill: 0));
        Assert.Equal(655_350, info.SampleRateHz);
        Assert.Equal(32, info.BitsPerSample);
        Assert.Equal(1, info.Channels);
        Assert.Equal(0xF_FFFF_FFFF, info.TotalSamples);
        Assert.False(info.HasAudioMd5);
    }

    [Fact]
    public void SkipsLeadingId3v2()
    {
        var id3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 1, 0x7F }; // size = 255
        var data = id3.Concat(new byte[255]).Concat(Header(44_100, 16)).ToArray();
        Assert.Equal(265, FlacStreamInfo.LeadingId3Length(data));
        Assert.Equal(44_100, FlacStreamInfo.Parse(data).SampleRateHz);
    }

    [Fact]
    public void Id3WithFooter_AddsTenBytes()
    {
        var id3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0x10, 0, 0, 0, 5 };
        Assert.Equal(25, FlacStreamInfo.LeadingId3Length(id3));
    }

    [Fact]
    public void NotFlac_Throws() =>
        Assert.Throws<FormatException>(() => FlacStreamInfo.Parse("RIFF....WAVEfmt "u8.ToArray().Concat(new byte[64]).ToArray()));

    [Fact]
    public void Truncated_Throws() =>
        Assert.Throws<FormatException>(() => FlacStreamInfo.Parse(Header(44_100, 16)[..30]));

    [Fact]
    public void StreamInfoNotFirst_Throws()
    {
        var data = Header(44_100, 16);
        data[4] = 0x04; // VORBIS_COMMENT block type
        Assert.Throws<FormatException>(() => FlacStreamInfo.Parse(data));
    }
}
