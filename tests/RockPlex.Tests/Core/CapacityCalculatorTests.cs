using RockPlex.Core.Models;
using RockPlex.Core.Selection;

namespace RockPlex.Tests.Core;

public sealed class CapacityCalculatorTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void Fits_WhenSelectionIsWithinFreeSpaceLessReserve()
    {
        var result = CapacityCalculator.Calculate(new DeviceCapacity(80 * GiB, 10 * GiB), 5 * GiB);
        Assert.True(result.Fits);
        Assert.Equal(0, result.OverBy);
        Assert.Equal(10 * GiB - CapacityCalculator.ReserveBytes, result.AvailableBytes);
    }

    [Fact]
    public void Over_ReportsTheOverage()
    {
        var result = CapacityCalculator.Calculate(new DeviceCapacity(80 * GiB, 10 * GiB), 12 * GiB);
        Assert.False(result.Fits);
        Assert.Equal(2 * GiB + CapacityCalculator.ReserveBytes, result.OverBy);
        Assert.True(result.FillRatio > 1);
    }

    [Fact]
    public void ExactlyAvailable_Fits()
    {
        var device = new DeviceCapacity(80 * GiB, 10 * GiB);
        var result = CapacityCalculator.Calculate(device, 10 * GiB - CapacityCalculator.ReserveBytes);
        Assert.True(result.Fits);
    }

    [Fact]
    public void ManagedBytes_CountAsAvailable()
    {
        var result = CapacityCalculator.Calculate(new DeviceCapacity(80 * GiB, 1 * GiB, 9 * GiB), 9 * GiB);
        Assert.True(result.Fits);
    }

    [Fact]
    public void NearlyFullDevice_NeverGoesNegative()
    {
        var result = CapacityCalculator.Calculate(new DeviceCapacity(80 * GiB, 1024), 0);
        Assert.Equal(0, result.AvailableBytes);
        Assert.True(result.Fits);
    }
}
