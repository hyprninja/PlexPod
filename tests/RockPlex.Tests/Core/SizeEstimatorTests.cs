using RockPlex.Core.Media;
using static RockPlex.Tests.Core.TestData;

namespace RockPlex.Tests.Core;

public sealed class SizeEstimatorTests
{
    [Fact]
    public void Copy_UsesPartSize_Exactly()
    {
        var estimate = SizeEstimator.EstimateBytes(Track("t1", size: 12_345, format: Mp3));
        Assert.Equal(12_345, estimate.Bytes);
        Assert.True(estimate.IsExact);
    }

    [Theory]
    [InlineData(24, 96_000, 2_400_000, 16.0 / 24 * 44_100 / 96_000)]
    [InlineData(24, 44_100, 2_400_000, 16.0 / 24)]
    [InlineData(16, 48_000, 2_400_000, 44_100.0 / 48_000)]
    [InlineData(24, 32_000, 2_400_000, 16.0 / 24)]
    [InlineData(16, 44_100, 2_400_000, 1.0)]
    public void Flac_ScalesByBitsAndRate(int bits, int rate, long size, double ratio)
    {
        var estimate = SizeEstimator.EstimateBytes(Track("t1", size: size, format: Flac(bits, rate)));
        Assert.Equal((long)Math.Ceiling(size * ratio), estimate.Bytes);
        Assert.True(estimate.IsExact);
    }

    [Fact]
    public void UnknownFlac_UsesPartSize_Conservatively()
    {
        var estimate = SizeEstimator.EstimateBytes(Track("t1", size: 50_000_000, format: Flac(null, null)));
        Assert.Equal(50_000_000, estimate.Bytes);
        Assert.False(estimate.IsExact);
        Assert.IsType<FormatDecision.UnknownFlac>(estimate.Decision);
    }

    [Fact]
    public void Override_ReplacesTrackFormat()
    {
        var track = Track("t1", size: 3_000_000, format: Flac(null, null));
        var estimate = SizeEstimator.EstimateBytes(track, Flac(24, 96_000));
        Assert.True(estimate.Bytes < 3_000_000);
        Assert.True(estimate.IsExact);
    }

    [Fact]
    public void Total_SkipsUnavailable_AndIsInexactWhenAnyIsUnknown()
    {
        var (bytes, exact) = SizeEstimator.EstimateTotal(
        [
            Track("t1", size: 100),
            Track("t2", size: 200, format: Flac(null, 44_100)),
            Track("t3", size: 999, available: false),
        ]);
        Assert.Equal(300, bytes);
        Assert.False(exact);
    }
}
