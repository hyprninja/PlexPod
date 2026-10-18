using PlexPod.Core.Models;

namespace PlexPod.Device;

/// <summary>Which firmware's library PlexPod writes, and so which sync workflow runs (M6 plan §2).</summary>
public enum IpodFirmware
{
    /// <summary>Rockbox: <c>/Music</c> folders, FLAC, <c>.m3u8</c> playlists, Rockbox's logs for stats.</summary>
    Rockbox,

    /// <summary>Apple's own firmware: <c>iPod_Control/Music/Fxx</c>, ALAC, the iTunesDB, Play Counts.</summary>
    Stock,
}

public sealed record IpodDevice(
    string DriveRoot,
    string VolumeLabel,
    long TotalBytes,
    long FreeBytes,
    IpodFirmware Firmware,
    string? RockboxTarget,
    string? RockboxVersion,
    SignedDatabase? SignedDatabase = null)
{
    /// <summary>The only Rockbox target PlexPod is built and tested for.</summary>
    public const string SupportedTarget = "ipodvideo";

    /// <summary>
    /// False for other Rockbox players (untested) and for an unreadable target. A stock iPod counts as
    /// supported unless something shows its firmware needs a signed database; its model often can't be
    /// told (the iPod Video's SysInfo is empty).
    /// </summary>
    public bool IsSupportedTarget => Firmware == IpodFirmware.Stock
        ? SignedDatabase is null
        : string.Equals(RockboxTarget, SupportedTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apple firmware that only accepts a database signed by iTunes (iPod Classic, nano 3G and later):
    /// PlexPod's database would be ignored, so the sync is blocked. Rockbox iPods are never blocked.
    /// </summary>
    public bool IsSyncBlocked => Firmware == IpodFirmware.Stock && SignedDatabase is not null;

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

public interface IIpodLocator
{
    IReadOnlyList<IpodDevice> FindDevices();
}

/// <summary>
/// Finds iPods: a drive whose root has <c>.rockbox</c> is a Rockbox iPod; one with <c>iPod_Control</c>
/// and no <c>.rockbox</c> runs Apple's stock firmware. Rockbox installs alongside the stock firmware
/// (which keeps <c>iPod_Control</c>), so <c>.rockbox</c> decides: a Rockbox iPod is never given an
/// Apple database by mistake. A stock iPod is also checked for firmware that needs a signed database
/// (<see cref="SignedDatabaseCheck"/>). Read-only: never writes to the device.
/// </summary>
public sealed class IpodLocator(IDriveSource drives, IUsbProductSource usb) : IIpodLocator
{
    public IpodLocator(IDriveSource drives)
        : this(drives, NoUsbProductSource.Instance)
    {
    }

    public const string RockboxFolder = ".rockbox";
    public const string InfoFile = "rockbox-info.txt";
    public const string StockFolder = "iPod_Control";

    public IReadOnlyList<IpodDevice> FindDevices()
    {
        var result = new List<IpodDevice>();
        foreach (var drive in drives.GetDrives())
        {
            if (TryRead(drive, usb) is { } device)
            {
                result.Add(device);
            }
        }

        return result;
    }

    public static IpodDevice? TryRead(DriveCandidate drive, IUsbProductSource? usb = null)
    {
        ArgumentNullException.ThrowIfNull(drive);
        try
        {
            var rockbox = Path.Combine(drive.RootPath, RockboxFolder);
            if (!Directory.Exists(rockbox))
            {
                if (!Directory.Exists(Path.Combine(drive.RootPath, StockFolder, "iTunes"))
                    || !Directory.Exists(Path.Combine(drive.RootPath, StockFolder, "Device")))
                {
                    return null;
                }

                var signed = SignedDatabaseCheck.Check(drive.RootPath, (usb ?? NoUsbProductSource.Instance).AppleProductId(drive.RootPath));
                return new IpodDevice(drive.RootPath, drive.VolumeLabel, drive.TotalBytes, drive.FreeBytes, IpodFirmware.Stock, null, null, signed);
            }

            var infoPath = Path.Combine(rockbox, InfoFile);
            var info = File.Exists(infoPath) ? RockboxInfoParser.Parse(File.ReadAllText(infoPath)) : RockboxInfo.Empty;
            return new IpodDevice(drive.RootPath, drive.VolumeLabel, drive.TotalBytes, drive.FreeBytes, IpodFirmware.Rockbox, info.Target, info.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unplugged mid-read, or still mounting; the next poll will try again.
            return null;
        }
    }
}
