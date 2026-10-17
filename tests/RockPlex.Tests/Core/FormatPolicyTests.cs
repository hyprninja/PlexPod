using RockPlex.Core.Media;
using RockPlex.Core.Models;
using static RockPlex.Tests.Core.TestData;

namespace RockPlex.Tests.Core;

public sealed class FormatPolicyTests
{
    [Theory]
    [InlineData(16, 44_100)]
    [InlineData(16, 32_000)]
    [InlineData(8, 22_050)]
    public void Flac_AtOrBelowTarget_IsCopied(int bits, int rate) =>
        Assert.IsType<FormatDecision.Copy>(FormatPolicy.Decide(Flac(bits, rate)));

    [Theory]
    [InlineData(16, 48_000, 16, 44_100)]
    [InlineData(24, 44_100, 16, 44_100)]
    [InlineData(24, 96_000, 16, 44_100)]
    [InlineData(24, 192_000, 16, 44_100)]
    [InlineData(24, 32_000, 16, 32_000)]
    [InlineData(32, 88_200, 16, 44_100)]
    public void Flac_AboveTarget_IsConvertedDown(int bits, int rate, int expectedBits, int expectedRate)
    {
        var decision = Assert.IsType<FormatDecision.ConvertFlac>(FormatPolicy.Decide(Flac(bits, rate)));
        Assert.Equal(expectedBits, decision.TargetBits);
        Assert.Equal(expectedRate, decision.TargetRateHz);
    }

    [Theory]
    [InlineData("mp3", "mp3")]
    [InlineData("mp4", "aac")]
    [InlineData("mp4", "alac")]
    [InlineData("ogg", "vorbis")]
    [InlineData("wav", "pcm")]
    public void NonFlac_IsAlwaysCopied_EvenWhenHiRes(string container, string codec) =>
        Assert.IsType<FormatDecision.Copy>(FormatPolicy.Decide(new AudioFormat(container, codec, 4600, 24, 192_000)));

    [Theory]
    [InlineData(null, null)]
    [InlineData(24, null)]
    [InlineData(null, 96_000)]
    [InlineData(0, 44_100)]
    public void Flac_WithUnknownBitsOrRate_IsUnknown(int? bits, int? rate) =>
        Assert.IsType<FormatDecision.UnknownFlac>(FormatPolicy.Decide(Flac(bits, rate)));

    [Fact]
    public void FlacContainer_WithoutCodec_IsTreatedAsFlac() =>
        Assert.IsType<FormatDecision.ConvertFlac>(FormatPolicy.Decide(new AudioFormat("FLAC", null, null, 24, 96_000)));

    [Fact]
    public void NeverRaisesBitsOrRate()
    {
        int[] depths = [1, 8, 12, 16, 20, 24, 32];
        int[] rates = [8_000, 11_025, 16_000, 22_050, 32_000, 44_100, 48_000, 88_200, 96_000, 176_400, 192_000, 384_000];
        foreach (var bits in depths)
        {
            foreach (var rate in rates)
            {
                switch (FormatPolicy.Decide(Flac(bits, rate)))
                {
                    case FormatDecision.ConvertFlac c:
                        Assert.True(c.TargetBits <= bits, $"{bits}/{rate}: bits raised");
                        Assert.True(c.TargetRateHz <= rate, $"{bits}/{rate}: rate raised");
                        Assert.True(c.TargetBits <= 16 && c.TargetRateHz <= 44_100);
                        Assert.False(c.TargetBits == bits && c.TargetRateHz == rate, "no-op conversion");
                        break;
                    case FormatDecision.Copy:
                        Assert.True(bits <= 16 && rate <= 44_100, $"{bits}/{rate} should convert");
                        break;
                    default:
                        Assert.Fail($"{bits}/{rate}: unexpected decision");
                        break;
                }
            }
        }
    }
}
