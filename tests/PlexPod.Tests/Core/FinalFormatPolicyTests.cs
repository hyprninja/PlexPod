using PlexPod.Core.Media;

namespace PlexPod.Tests.Core;

public sealed class FinalFormatPolicyTests
{
    private static FlacAction Decide(int bits, int rate, int maxBlock = 4096) =>
        FinalFormatPolicy.Decide(new FlacStreamInfo(4096, maxBlock, rate, 2, bits, 1000, "00"));

    [Theory]
    [InlineData(16, 44_100)]
    [InlineData(16, 32_000)]
    [InlineData(16, 22_050)]
    [InlineData(8, 22_050)]
    public void AtOrBelowTarget_IsCopied(int bits, int rate) =>
        Assert.IsType<FlacAction.Copy>(Decide(bits, rate));

    [Theory]
    [InlineData(16, 48_000, 16, 44_100)]
    [InlineData(24, 44_100, 16, 44_100)]
    [InlineData(24, 96_000, 16, 44_100)]
    [InlineData(24, 192_000, 16, 44_100)]
    [InlineData(24, 32_000, 16, 32_000)]
    public void AboveTarget_IsConverted(int bits, int rate, int targetBits, int targetRate) =>
        Assert.Equal(new FlacAction.Convert(targetBits, targetRate), Decide(bits, rate));

    [Fact]
    public void LargeBlocks_OnACopy_AreReencodedLosslessly()
    {
        Assert.IsType<FlacAction.ReencodeLossless>(Decide(16, 44_100, maxBlock: 8192));
        Assert.IsType<FlacAction.Copy>(Decide(16, 44_100, maxBlock: FinalFormatPolicy.MaxSafeBlockSize));
    }

    [Fact]
    public void LargeBlocks_OnAConversion_StayAConversion() =>
        Assert.IsType<FlacAction.Convert>(Decide(24, 96_000, maxBlock: 16384));

    [Fact]
    public void NeverRaisesBitsOrRate()
    {
        int[] depths = [4, 8, 12, 16, 20, 24, 32];
        int[] rates = [8_000, 11_025, 22_050, 32_000, 44_100, 48_000, 88_200, 96_000, 176_400, 192_000, 384_000];
        foreach (var bits in depths)
        {
            foreach (var rate in rates)
            {
                if (Decide(bits, rate) is FlacAction.Convert c)
                {
                    Assert.True(c.TargetBits <= bits && c.TargetRateHz <= rate, $"{bits}/{rate} raised to {c.TargetBits}/{c.TargetRateHz}");
                    Assert.True(c.TargetBits <= 16 && c.TargetRateHz <= 44_100);
                }
                else
                {
                    Assert.True(bits <= 16 && rate <= 44_100, $"{bits}/{rate} should convert");
                }
            }
        }
    }
}
