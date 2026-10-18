using PlexPod.Core.Models;

namespace PlexPod.Core.Selection;

public sealed record CapacityResult(long AvailableBytes, long SelectedBytes, bool Fits, long OverBy)
{
    /// <summary>Selected / available, for the capacity bar. May exceed 1.</summary>
    public double FillRatio => AvailableBytes > 0
        ? (double)SelectedBytes / AvailableBytes
        : SelectedBytes > 0 ? 1 : 0;
}

public static class CapacityCalculator
{
    /// <summary>
    /// Kept free for Rockbox's database, playlists and the manifest, so the fit check never
    /// promises the last few megabytes of the disk.
    /// </summary>
    public const long ReserveBytes = 64L * 1024 * 1024;

    public static CapacityResult Calculate(DeviceCapacity device, ResolvedSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return Calculate(device, selection.TotalEstimatedBytes);
    }

    public static CapacityResult Calculate(DeviceCapacity device, long selectedBytes)
    {
        ArgumentNullException.ThrowIfNull(device);

        // M1 has no manifest, so ManagedBytes is 0 and available is free space minus the reserve.
        var available = Math.Max(0, device.FreeBytes + device.ManagedBytes - ReserveBytes);
        var over = Math.Max(0, selectedBytes - available);
        return new CapacityResult(available, selectedBytes, over == 0, over);
    }
}
