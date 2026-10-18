using RockPlex.Core.Models;

namespace RockPlex.Device;

public sealed record RockboxDevice(
    string DriveRoot,
    string VolumeLabel,
    long TotalBytes,
    long FreeBytes,
    string? RockboxTarget,
    string? RockboxVersion)
{
    /// <summary>The only target RockPlex is built and tested for.</summary>
    public const string SupportedTarget = "ipodvideo";

    /// <summary>False for other Rockbox players (untested) and for an unreadable target.</summary>
    public bool IsSupportedTarget => string.Equals(RockboxTarget, SupportedTarget, StringComparison.OrdinalIgnoreCase);

    public DeviceCapacity Capacity => new(TotalBytes, FreeBytes);
}

/// <summary>A mounted volume that might be a Rockbox player.</summary>
public sealed record DriveCandidate(string RootPath, string VolumeLabel, long TotalBytes, long FreeBytes);

public interface IDriveSource
{
    IEnumerable<DriveCandidate> GetDrives();
}

/// <summary>Ready local drives, skipping network and optical drives.</summary>
public sealed class SystemDriveSource : IDriveSource
{
    public IEnumerable<DriveCandidate> GetDrives()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            DriveCandidate? candidate = null;
            try
            {
                // Type first: IsReady on a disconnected mapped network drive can block for ~20 s (SMB timeout).
                if (drive.DriveType is not (DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory) && drive.IsReady)
                {
                    candidate = new DriveCandidate(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize, drive.AvailableFreeSpace);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The drive vanished or is locked between enumeration and query; skip it.
            }

            if (candidate is not null)
            {
                yield return candidate;
            }
        }
    }
}

public interface IRockboxDeviceLocator
{
    IReadOnlyList<RockboxDevice> FindDevices();
}

/// <summary>Finds drives whose root contains <c>.rockbox</c>. Read-only: never writes to the device.</summary>
public sealed class RockboxDeviceLocator(IDriveSource drives) : IRockboxDeviceLocator
{
    public const string RockboxFolder = ".rockbox";
    public const string InfoFile = "rockbox-info.txt";

    public IReadOnlyList<RockboxDevice> FindDevices()
    {
        var result = new List<RockboxDevice>();
        foreach (var drive in drives.GetDrives())
        {
            if (TryRead(drive) is { } device)
            {
                result.Add(device);
            }
        }

        return result;
    }

    public static RockboxDevice? TryRead(DriveCandidate drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        try
        {
            var rockbox = Path.Combine(drive.RootPath, RockboxFolder);
            if (!Directory.Exists(rockbox))
            {
                return null;
            }

            var infoPath = Path.Combine(rockbox, InfoFile);
            var info = File.Exists(infoPath) ? RockboxInfoParser.Parse(File.ReadAllText(infoPath)) : RockboxInfo.Empty;
            return new RockboxDevice(drive.RootPath, drive.VolumeLabel, drive.TotalBytes, drive.FreeBytes, info.Target, info.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unplugged mid-read, or still mounting; the next poll will try again.
            return null;
        }
    }
}
