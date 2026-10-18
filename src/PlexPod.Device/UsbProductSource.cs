using Microsoft.Win32;

namespace PlexPod.Device;

/// <summary>The USB product id of the Apple device behind a drive, when the system knows it.</summary>
public interface IUsbProductSource
{
    int? AppleProductId(string driveRoot);
}

/// <summary>
/// Looks the id up in the registry, read-only: <c>MountedDevices</c> names the USB storage device
/// behind the drive letter (<c>USBSTOR#Disk&amp;Ven_Apple&amp;Prod_iPod&amp;Rev_x#{serial}&amp;0#…</c>),
/// and the serial (the iPod's FireWire GUID) is a key under <c>Enum\USB\VID_05AC&amp;PID_xxxx</c>.
/// Anything unexpected → null, which is no evidence either way.
/// </summary>
public sealed class RegistryUsbProductSource : IUsbProductSource
{
    private const string AppleUsbPrefix = "VID_05AC&PID_";

    public int? AppleProductId(string driveRoot)
    {
        ArgumentNullException.ThrowIfNull(driveRoot);
        if (!OperatingSystem.IsWindows() || driveRoot.Length < 2 || !char.IsAsciiLetter(driveRoot[0]) || driveRoot[1] != ':')
        {
            return null;
        }

        try
        {
            using var mounted = Registry.LocalMachine.OpenSubKey(@"SYSTEM\MountedDevices");
            if (mounted?.GetValue($@"\DosDevices\{char.ToUpperInvariant(driveRoot[0])}:") is not byte[] data
                || SerialFromMountedDevice(System.Text.Encoding.Unicode.GetString(data)) is not { } serial)
            {
                return null;
            }

            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (var name in usb?.GetSubKeyNames() ?? [])
            {
                // Composite children (…&MI_00) share the serial with their parent; only the device itself counts.
                if (name.Length == AppleUsbPrefix.Length + 4
                    && name.StartsWith(AppleUsbPrefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.AsSpan(AppleUsbPrefix.Length), System.Globalization.NumberStyles.HexNumber, null, out var pid))
                {
                    using var device = usb!.OpenSubKey($@"{name}\{serial}");
                    if (device is not null)
                    {
                        return pid;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }

        return null;
    }

    /// <summary>The serial in an Apple USB storage device path, or null for anything else.</summary>
    internal static string? SerialFromMountedDevice(string path)
    {
        // _??_USBSTOR#Disk&Ven_Apple&Prod_iPod&Rev_1.62#000A27001610F1D5&0#{53f56307-…}
        var parts = path.TrimEnd('\0').Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("USBSTOR", StringComparison.OrdinalIgnoreCase)
            || !parts[1].Contains("&Ven_Apple&", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var instance = parts[2];
        var amp = instance.LastIndexOf('&');
        var serial = amp > 0 ? instance[..amp] : instance;
        return serial.Length is > 0 and <= 64 && serial.All(char.IsAsciiLetterOrDigit) ? serial : null;
    }
}

/// <summary>For tests and non-Windows hosts: no USB evidence.</summary>
public sealed class NoUsbProductSource : IUsbProductSource
{
    public static readonly NoUsbProductSource Instance = new();

    public int? AppleProductId(string driveRoot) => null;
}
