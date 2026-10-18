namespace PlexPod.Core.Models;

/// <summary>
/// Space on the iPod. <see cref="ManagedBytes"/> is space already used by PlexPod-managed files,
/// which a sync may replace; it is always 0 until the manifest exists (M3).
/// </summary>
public sealed record DeviceCapacity(long TotalBytes, long FreeBytes, long ManagedBytes = 0);
