using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PlexPod.Device.Sync;

/// <summary>The file copied to the iPod didn't read back identical; nothing was renamed into place.</summary>
public sealed class DeviceVerifyException(string message) : IOException(message);

public sealed record CopyResult(string Sha256, long SizeBytes, IReadOnlyList<string> CreatedFolders);

/// <summary>
/// The only code that changes files on the iPod. Every write goes to <c>&lt;path&gt;.plexpod-tmp</c>,
/// write-through and flushed, then is re-read straight from the device (unbuffered, bypassing the
/// Windows cache) and compared by SHA-256 before it is renamed into place (owner decision D6).
/// Callers only pass paths the manifest owns.
/// </summary>
public sealed class DeviceFileOps
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING
    private const int Alignment = 4096;
    private const int BufferSize = 1 << 20;

    public async Task<CopyResult> CopyVerifiedAsync(string localSource, string driveRoot, string devicePath, CancellationToken ct)
    {
        DevicePaths.EnsureManaged(devicePath);
        var final = DevicePaths.ToLocal(driveRoot, devicePath);
        var created = EnsureFolders(driveRoot, devicePath);
        var temp = final + DevicePaths.TempSuffix;
        try
        {
            string written;
            long size;
            var input = new FileStream(localSource, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough | FileOptions.Asynchronous);
                await using (output.ConfigureAwait(false))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[BufferSize];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }

                    output.Flush(flushToDisk: true);
                    size = output.Length;
                    written = Convert.ToHexStringLower(hash.GetHashAndReset());
                }
            }

            var readBack = await Task.Run(() => HashFromDevice(temp), ct).ConfigureAwait(false);
            if (!string.Equals(written, readBack, StringComparison.Ordinal))
            {
                throw new DeviceVerifyException($"{devicePath} didn't read back identical from the iPod (possible bad sector or USB error).");
            }

            File.Move(temp, final, overwrite: true);
            return new CopyResult(written, size, created);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Writes a small text file (a playlist) the same verified way. UTF-8 without BOM.</summary>
    public async Task<CopyResult> WriteTextVerifiedAsync(string content, string driveRoot, string devicePath, CancellationToken ct)
    {
        DevicePaths.EnsureManaged(devicePath);
        var final = DevicePaths.ToLocal(driveRoot, devicePath);
        var created = EnsureFolders(driveRoot, devicePath);
        var temp = final + DevicePaths.TempSuffix;
        try
        {
            await DeviceStore.WriteDurableAsync(temp, content, ct).ConfigureAwait(false);
            var expected = Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(content)));
            if (!string.Equals(expected, await Task.Run(() => HashFromDevice(temp), ct).ConfigureAwait(false), StringComparison.Ordinal))
            {
                throw new DeviceVerifyException($"{devicePath} didn't read back identical from the iPod.");
            }

            File.Move(temp, final, overwrite: true);
            return new CopyResult(expected, new FileInfo(final).Length, created);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Renames a PlexPod file on the device (no re-copy). The target must be free.</summary>
    public IReadOnlyList<string> Move(string driveRoot, string fromDevicePath, string toDevicePath)
    {
        DevicePaths.EnsureManaged(fromDevicePath);
        DevicePaths.EnsureManaged(toDevicePath);
        var created = EnsureFolders(driveRoot, toDevicePath);
        var from = DevicePaths.ToLocal(driveRoot, fromDevicePath);
        var to = DevicePaths.ToLocal(driveRoot, toDevicePath);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only change on FAT32: go via a temporary name.
            var temp = to + DevicePaths.TempSuffix;
            File.Move(from, temp);
            File.Move(temp, to);
        }
        else
        {
            File.Move(from, to, overwrite: false);
        }

        return created;
    }

    public static void Delete(string driveRoot, string devicePath)
    {
        DevicePaths.EnsureManaged(devicePath);
        var path = DevicePaths.ToLocal(driveRoot, devicePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Removes the given PlexPod-created folders that are now empty, deepest first. Returns those removed.</summary>
    public static IReadOnlyList<string> DeleteEmptyFolders(string driveRoot, IEnumerable<string> folders)
    {
        var removed = new List<string>();
        foreach (var folder in folders.Where(DevicePaths.IsManaged).OrderByDescending(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.Ordinal))
        {
            var local = DevicePaths.ToLocal(driveRoot, folder);
            try
            {
                if (Directory.Exists(local) && !Directory.EnumerateFileSystemEntries(local).Any())
                {
                    Directory.Delete(local);
                    removed.Add(folder);
                }
            }
            catch (IOException)
            {
                // In use or not empty after all; leave it.
            }
        }

        return removed;
    }

    /// <summary>Deletes leftover <c>*.plexpod-tmp</c> files from an interrupted sync. The suffix is PlexPod's own.</summary>
    public static int CleanTempFiles(string driveRoot)
    {
        var count = 0;
        foreach (var folder in new[] { "Music", "Playlists", "iPod_Control/Music", "iPod_Control/iTunes", DeviceStore.FolderName, DeviceStore.LegacyFolderName })
        {
            var local = Path.Combine(driveRoot, folder);
            if (!Directory.Exists(local))
            {
                continue;
            }

            foreach (var suffix in new[] { DevicePaths.TempSuffix, DevicePaths.LegacyTempSuffix })
            {
                foreach (var file in Directory.EnumerateFiles(local, "*" + suffix, SearchOption.AllDirectories))
                {
                    TryDelete(file);
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>Device paths (exact case as stored on disk) of every file under the given top-level folders.</summary>
    public static IReadOnlyList<string> ListFiles(string driveRoot, params string[] topFolders)
    {
        var result = new List<string>();
        foreach (var top in topFolders)
        {
            var local = Path.Combine(driveRoot, top);
            if (Directory.Exists(local))
            {
                result.AddRange(Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(DevicePaths.TempSuffix, StringComparison.OrdinalIgnoreCase)
                        && !f.EndsWith(DevicePaths.LegacyTempSuffix, StringComparison.OrdinalIgnoreCase))
                    .Select(f => DevicePaths.ToDevice(driveRoot, f)));
            }
        }

        return result;
    }

    public static long FreeBytes(string driveRoot) => new DriveInfo(driveRoot).AvailableFreeSpace;

    /// <summary>SHA-256 read straight from the device, bypassing the Windows file cache when the volume allows it.</summary>
    public static string HashFromDevice(string path)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return HashBuffered(path);
        }

        using (handle)
        {
            return HashUnbuffered(handle);
        }
    }

    private static unsafe string HashUnbuffered(SafeFileHandle handle)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = NativeMemory.AlignedAlloc(BufferSize, Alignment);
        try
        {
            var span = new Span<byte>(buffer, BufferSize);
            long offset = 0;
            int read;
            while ((read = RandomAccess.Read(handle, span, offset)) > 0)
            {
                hash.AppendData(span[..read]);
                offset += read;
                if (read < BufferSize)
                {
                    break;
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string HashBuffered(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static List<string> EnsureFolders(string driveRoot, string devicePath)
    {
        var created = new List<string>();
        foreach (var folder in DevicePaths.ParentFolders(devicePath))
        {
            var local = DevicePaths.ToLocal(driveRoot, folder);
            if (!Directory.Exists(local))
            {
                Directory.CreateDirectory(local);
                created.Add(folder);
            }
        }

        return created;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
