using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RockPlex.Core.Models;
using RockPlex.Core.Tagging;

namespace RockPlex.App.Services;

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
}

/// <summary>
/// <c>settings.json</c>, written atomically (temp file, then replace). Never holds the Plex token.
/// </summary>
public sealed class SettingsStore(AppPaths paths, ILogger<SettingsStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private AppSettings? _current;

    public AppSettings Current => _current ??= Load();

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var updated = change(Current);
            _current = updated;
            await WriteAsync(updated, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private AppSettings Load()
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
            logger.LogWarning(ex, "settings.json unreadable; starting fresh");
        }

        // First run (or unreadable file): create and persist a new client identifier straight away.
        // Written synchronously: Load can run on the UI thread, where blocking on async I/O would deadlock.
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
