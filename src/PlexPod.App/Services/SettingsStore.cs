using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PlexPod.Core.Models;
using PlexPod.Core.Stats;
using PlexPod.Core.Tagging;

namespace PlexPod.App.Services;

public sealed record AppSettings
{
    /// <summary>Stable per-install GUID sent as <c>X-Plex-Client-Identifier</c>.</summary>
    public string ClientIdentifier { get; init; } = Guid.NewGuid().ToString();

    public string? ServerMachineId { get; init; }

    public string? ServerUrlOverride { get; init; }

    public string? SectionKey { get; init; }

    public SelectionProfile? Selection { get; init; }

    /// <summary>ffmpeg.exe or its folder; empty means "find it" (PATH, then winget).</summary>
    public string? FfmpegPath { get; init; }

    public int ConversionCacheCapGb { get; init; } = 20;

    /// <summary>Which tag fields are written to the iPod copies (D4).</summary>
    public TagPolicy TagPolicy { get; init; } = TagPolicy.Default;

    /// <summary>Send plays and ratings from the iPod to Plex at every sync (M4, D11).</summary>
    public bool StatsReturn { get; init; } = true;

    /// <summary>D10: whether a rating changed on the iPod replaces Plex's, or only fills gaps.</summary>
    public RatingMode RatingsFromIpod { get; init; } = RatingMode.IpodWins;
}

/// <summary>
/// <c>settings.json</c>, written atomically (temp file, then replace). Never holds the Plex token.
/// More than one PlexPod can be open (an installed copy and a dev build, or a second window), so the
/// file is the only truth: every change re-reads it under a lock shared between processes and changes
/// just its own setting, and <see cref="Current"/> picks up what another copy saved.
/// </summary>
public sealed class SettingsStore(AppPaths paths, ILogger<SettingsStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppSettings? _current;
    private (DateTime WrittenUtc, long Length) _loadedStamp;

    /// <summary>How long a save waits for another PlexPod to finish its own before going ahead anyway.</summary>
    internal TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The settings as last saved by any PlexPod on this PC.</summary>
    public AppSettings Current
    {
        get
        {
            var stamp = Stamp();
            if (_current is null || (stamp is { } s && s != _loadedStamp))
            {
                _current = Read() ?? _current ?? CreateFresh();
                _loadedStamp = Stamp() ?? default;
            }

            return _current;
        }
    }

    /// <summary>
    /// Applies <paramref name="change"/> to the settings as they are on disk now, so a change saved by
    /// another PlexPod since this one loaded is kept, not overwritten with this copy's older values.
    /// </summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var crossProcess = await LockAsync(ct).ConfigureAwait(false);
            var updated = change(Read() ?? Current);
            await WriteAsync(updated, ct).ConfigureAwait(false);
            _current = updated;
            _loadedStamp = Stamp() ?? default;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private (DateTime, long)? Stamp()
    {
        try
        {
            var info = new FileInfo(paths.Settings);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The saved settings; null when there are none yet or the file can't be read.</summary>
    private AppSettings? Read()
    {
        try
        {
            if (File.Exists(paths.Settings))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.Settings), Json);
                if (loaded is not null && !string.IsNullOrWhiteSpace(loaded.ClientIdentifier))
                {
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "settings.json unreadable");
        }

        return null;
    }

    /// <summary>
    /// First run (or unreadable file): create and persist a new client identifier straight away.
    /// Written synchronously: this can run on the UI thread, where blocking on async I/O would deadlock.
    /// </summary>
    private AppSettings CreateFresh()
    {
        var fresh = new AppSettings();
        try
        {
            var temp = paths.Settings + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(fresh, Json));
            File.Move(temp, paths.Settings, overwrite: true);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Could not write settings.json");
        }

        return fresh;
    }

    /// <summary>
    /// An exclusive handle on <c>settings.json.lock</c>, held while one PlexPod reads, changes and
    /// writes the settings. If another copy holds it for longer than <see cref="LockTimeout"/>, the
    /// save goes ahead unlocked: a lost race is better than a setting that never saves.
    /// </summary>
    private async Task<IDisposable?> LockAsync(CancellationToken ct)
    {
        var path = paths.Settings + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Saving settings without the lock");
                return null;
            }
        }
    }

    private async Task WriteAsync(AppSettings settings, CancellationToken ct)
    {
        var temp = paths.Settings + ".tmp";
        var stream = File.Create(temp);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings, Json, ct).ConfigureAwait(false);
        }

        File.Move(temp, paths.Settings, overwrite: true);
    }
}
