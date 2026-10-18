using System.Text;
using System.Text.Json;
using PlexPod.Core.Stats;
using PlexPod.Core.Sync;

namespace PlexPod.Device.Sync;

public enum ManifestSource
{
    /// <summary>No manifest on the device (first sync, or both copies unreadable).</summary>
    None,
    Current,

    /// <summary><c>manifest.json</c> was unreadable; recovered from <c>manifest.prev.json</c>.</summary>
    Previous,
}

/// <summary>The iPod's identity, at <c>/.plexpod/device.json</c>.</summary>
public sealed record DeviceIdentity(string DeviceId, string? VolumeLabel, string? RockboxTarget, DateTimeOffset FirstSync);

/// <summary>
/// PlexPod's own files on the iPod, under <c>/.plexpod/</c>. Every write is temp file + rename, and
/// the manifest's previous version is kept as <c>manifest.prev.json</c> for recovery.
/// </summary>
public sealed class DeviceStore(string driveRoot)
{
    public const string FolderName = ".plexpod";

    /// <summary>The folder's name before the rebrand. Read when it's the only one; moved to <see cref="FolderName"/> before any write.</summary>
    public const string LegacyFolderName = ".rockplex";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string DriveRoot { get; } = driveRoot;

    /// <summary><c>/.plexpod</c>, or <c>/.rockplex</c> while that is the only one (an iPod synced before the rename).</summary>
    public string Folder =>
        !Directory.Exists(Path.Combine(DriveRoot, FolderName)) && Directory.Exists(LegacyFolder)
            ? LegacyFolder
            : Path.Combine(DriveRoot, FolderName);

    private string LegacyFolder => Path.Combine(DriveRoot, LegacyFolderName);

    /// <summary>
    /// Renames <c>/.rockplex</c> to <c>/.plexpod</c> when only the old one exists. Every write calls this
    /// first, so reads (planning, plug-in detection) never change the iPod. Returns whether it moved.
    /// </summary>
    public bool MigrateLegacyFolder()
    {
        var current = Path.Combine(DriveRoot, FolderName);
        if (Directory.Exists(current) || !Directory.Exists(LegacyFolder))
        {
            return false;
        }

        Directory.Move(LegacyFolder, current);
        return true;
    }

    private string ManifestPath => Path.Combine(Folder, "manifest.json");

    private string PreviousManifestPath => Path.Combine(Folder, "manifest.prev.json");

    private string DevicePath => Path.Combine(Folder, "device.json");

    private string StatsPath => Path.Combine(Folder, "stats.json");

    public async Task<(DeviceManifest? Manifest, ManifestSource Source)> LoadManifestAsync(CancellationToken ct)
    {
        if (await TryReadAsync<DeviceManifest>(ManifestPath, ct).ConfigureAwait(false) is { } current)
        {
            return (current, ManifestSource.Current);
        }

        if (await TryReadAsync<DeviceManifest>(PreviousManifestPath, ct).ConfigureAwait(false) is { } previous)
        {
            return (previous, ManifestSource.Previous);
        }

        return (null, ManifestSource.None);
    }

    /// <summary>Writes the manifest atomically, keeping the last good one as <c>manifest.prev.json</c>.</summary>
    public async Task SaveManifestAsync(DeviceManifest manifest, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        MigrateLegacyFolder();
        Directory.CreateDirectory(Folder);
        var json = JsonSerializer.Serialize(manifest with { UpdatedAt = DateTimeOffset.Now }, Json);
        var temp = ManifestPath + DevicePaths.TempSuffix;
        await WriteDurableAsync(temp, json, ct).ConfigureAwait(false);
        if (File.Exists(ManifestPath))
        {
            File.Copy(ManifestPath, PreviousManifestPath, overwrite: true);
        }

        File.Move(temp, ManifestPath, overwrite: true);
    }

    public async Task<DeviceIdentity> LoadOrCreateIdentityAsync(string? volumeLabel, string? rockboxTarget, CancellationToken ct)
    {
        if (await TryReadAsync<DeviceIdentity>(DevicePath, ct).ConfigureAwait(false) is { DeviceId.Length: > 0 } existing)
        {
            return existing;
        }

        var created = new DeviceIdentity(Guid.NewGuid().ToString(), volumeLabel, rockboxTarget, DateTimeOffset.Now);
        await WriteAtomicAsync(DevicePath, JsonSerializer.Serialize(created, Json), ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Reads <c>device.json</c> without creating it (for plug-in detection).</summary>
    public Task<DeviceIdentity?> TryReadIdentityAsync(CancellationToken ct) => TryReadAsync<DeviceIdentity>(DevicePath, ct);

    /// <summary>Stats-return bookmarks and last-sent ratings (M4). Unreadable → empty, which at worst resends plays once.</summary>
    public async Task<StatsState> LoadStatsAsync(CancellationToken ct) =>
        await TryReadAsync<StatsState>(StatsPath, ct).ConfigureAwait(false) is { } state
            ? state with
            {
                // The file is untrusted: drop null or nonsense entries.
                Bookmarks = (state.Bookmarks ?? []).Where(b => b is { PrefixHash.Length: 64, FileName: not null, ProcessedBytes: > 0 }).ToList(),
                RatingsSent = state.RatingsSent ?? new Dictionary<string, int>(),
                PendingPlays = (state.PendingPlays ?? new Dictionary<string, int>()).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => Math.Min(p.Value, 10_000)),
                PendingRatings = (state.PendingRatings ?? new Dictionary<string, int>()).Where(p => p.Value is >= -1 and <= 10).ToDictionary(p => p.Key, p => p.Value),
            }
            : StatsState.Empty;

    public Task SaveStatsAsync(StatsState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        return WriteAtomicAsync(StatsPath, JsonSerializer.Serialize(state, Json), ct);
    }

    public Task WriteTextAsync(string fileName, string content, CancellationToken ct) =>
        WriteAtomicAsync(Path.Combine(Folder, fileName), content, ct);

    public async Task<string?> TryReadTextAsync(string fileName, CancellationToken ct)
    {
        var path = Path.Combine(Folder, fileName);
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        if (MigrateLegacyFolder())
        {
            path = Path.Combine(Folder, Path.GetFileName(path)); // it was resolved to the old folder
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + DevicePaths.TempSuffix;
        await WriteDurableAsync(temp, content, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Write-through and flushed to the device before returning.</summary>
    internal static async Task WriteDurableAsync(string path, string content, CancellationToken ct)
    {
        var bytes = new UTF8Encoding(false).GetBytes(content);
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough | FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
