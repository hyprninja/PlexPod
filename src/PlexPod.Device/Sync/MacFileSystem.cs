using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlexPod.Device.Sync;

/// <summary>
/// What macOS does differently on the iPod's FAT32 volume (M7 plan §3), measured on a FAT32 image on
/// 14 Nov 2026:
/// <list type="bullet">
/// <item>Directory listings return names <b>decomposed</b> (NFD) even when they were written composed
/// (NFC); lookups match either form. Names read from a listing are turned back into NFC, which is what
/// Plex, the manifest and the playlists use.</item>
/// <item>Every file and folder a process creates gets a <c>com.apple.provenance</c> attribute that
/// can't be removed, and FAT32 stores it in a <c>._name</c> twin file. A Rockbox database scan would
/// list <c>._track.flac</c> as a broken track, so PlexPod deletes the twin of everything it writes
/// (deleting the twin is allowed and leaves the real file clean).</item>
/// </list>
/// Everything here does nothing on Windows, so the Windows behaviour is unchanged.
/// </summary>
public static partial class MacFileSystem
{
    public const string AppleDoublePrefix = "._";

    /// <summary>Folders and files macOS keeps on volumes for itself (Finder, Spotlight, the Trash).</summary>
    private static readonly HashSet<string> MetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", ".Spotlight-V100", ".fseventsd", ".Trashes", ".TemporaryItems", ".DocumentRevisions-V100", ".apdisk",
    };

    private const int FNoCache = 48; // F_NOCACHE in <sys/fcntl.h>

    /// <summary>
    /// True for macOS's own files (<c>._*</c> twins, <c>.DS_Store</c>, Spotlight and Trash folders): never a
    /// track, a playlist or one of the user's files. Applies on every OS, as a Mac may have visited the iPod.
    /// </summary>
    public static bool IsMacMetadata(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith(AppleDoublePrefix, StringComparison.Ordinal) || MetadataNames.Contains(name);
    }

    /// <summary>A name or path read from a directory listing, in the form PlexPod writes and compares (NFC on macOS).</summary>
    public static string FromListing(string name) => FromListing(name, OperatingSystem.IsMacOS());

    internal static string FromListing(string name, bool mac)
    {
        ArgumentNullException.ThrowIfNull(name);
        return mac && !name.IsNormalized(NormalizationForm.FormC) ? name.Normalize(NormalizationForm.FormC) : name;
    }

    /// <summary>Deletes the <c>._</c> twin of a file or folder PlexPod just wrote (macOS only; a no-op elsewhere).</summary>
    public static void RemoveTwin(string localPath) => RemoveTwin(localPath, OperatingSystem.IsMacOS());

    internal static void RemoveTwin(string localPath, bool mac)
    {
        ArgumentException.ThrowIfNullOrEmpty(localPath);
        if (!mac)
        {
            return;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(localPath);
        var folder = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) || name.StartsWith(AppleDoublePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var twin = Path.Combine(folder, AppleDoublePrefix + name);
        try
        {
            if (File.Exists(twin))
            {
                File.Delete(twin);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a twin left behind is untidy, not harmful to the file itself.
        }
    }

    /// <summary>
    /// Turns the file cache off for <paramref name="handle"/> (<c>F_NOCACHE</c>), so writes aren't kept in
    /// memory and reads come from the disk: macOS's counterpart of <c>FILE_FLAG_NO_BUFFERING</c>. Returns
    /// false where it doesn't apply or failed; the read-back check still runs either way.
    /// </summary>
    public static bool DisableCache(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsMacOS() || handle.IsInvalid)
        {
            return false;
        }

        var fd = (int)handle.DangerousGetHandle();

        // fcntl is variadic. On Apple Silicon variadic arguments are passed on the stack, so the value
        // goes in the ninth slot (the first eight are registers); on Intel it's an ordinary third argument.
        var result = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? FcntlArm64(fd, FNoCache, 0, 0, 0, 0, 0, 0, 1)
            : FcntlX64(fd, FNoCache, 1);
        return result != -1;
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlArm64(int fd, int cmd, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, nint value);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fcntl", SetLastError = true)]
    private static partial int FcntlX64(int fd, int cmd, nint value);
}
