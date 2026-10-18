using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace RockPlex.Media.Ffmpeg;

/// <summary>A usable ffmpeg. Conversions need <see cref="HasSoxr"/>; copies and verification don't.</summary>
public sealed record FfmpegInstall(string Path, string Version, bool HasSoxr)
{
    public string Description => $"ffmpeg {Version}{(HasSoxr ? " with soxr ✓" : " (no soxr: FLAC above 44.1 kHz can't be converted)")}";
}

/// <summary>
/// Finds ffmpeg: the configured path (file or folder), then <c>PATH</c>, then winget's install
/// locations. RockPlex never bundles or downloads ffmpeg.
/// </summary>
public sealed partial class FfmpegLocator(FfmpegRunner runner, ILogger<FfmpegLocator> logger)
{
    public const string ExecutableName = "ffmpeg.exe";

    public async Task<FfmpegInstall?> LocateAsync(string? configuredPath, CancellationToken ct)
    {
        foreach (var candidate in Candidates(configuredPath))
        {
            var install = await ProbeAsync(candidate, ct).ConfigureAwait(false);
            if (install is not null)
            {
                logger.LogInformation("Using {Description} at {Path}", install.Description, install.Path);
                return install;
            }
        }

        logger.LogWarning("ffmpeg not found");
        return null;
    }

    public async Task<FfmpegInstall?> ProbeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var result = await runner.RunAsync(path, ["-hide_banner", "-version"], ct, throwOnError: false).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var version = VersionPattern().Match(result.StandardOutput) is { Success: true } m ? m.Groups[1].Value : "unknown";
            return new FfmpegInstall(path, version, result.StandardOutput.Contains("--enable-libsoxr", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("ffmpeg probe failed for {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    internal static IEnumerable<string> Candidates(string? configuredPath) =>
        Candidates(configuredPath, Environment.GetEnvironmentVariable("PATH"));

    internal static IEnumerable<string> Candidates(string? configuredPath, string? pathVariable)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var trimmed = configuredPath.Trim().Trim('"');
            yield return Directory.Exists(trimmed) ? System.IO.Path.Combine(trimmed, ExecutableName) : trimmed;
        }

        foreach (var entry in (pathVariable ?? string.Empty).Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            // Relative entries (".", "bin") would run whatever ffmpeg.exe sits in the current folder.
            var dir = entry.Trim().Trim('"');
            if (System.IO.Path.IsPathFullyQualified(dir))
            {
                yield return System.IO.Path.Combine(dir, ExecutableName);
            }
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links", ExecutableName);

        var packages = System.IO.Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var dir in Directory.EnumerateDirectories(packages, "*FFmpeg*").Order(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var exe in SafeFind(dir))
                {
                    yield return exe;
                }
            }
        }
    }

    private static IEnumerable<string> SafeFind(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, ExecutableName, SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^ffmpeg version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
