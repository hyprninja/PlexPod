using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RockPlex.Media.Ffmpeg;

public sealed record FfmpegResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>ffmpeg failed; <see cref="Exception.Message"/> carries the last lines of its stderr.</summary>
public sealed class FfmpegException(string message) : Exception(message);

/// <summary>
/// Runs ffmpeg/ffprobe as a child process with arguments passed as a list (no shell quoting).
/// Cancelling kills the whole process tree.
/// </summary>
public sealed class FfmpegRunner(ILogger<FfmpegRunner> logger)
{
    private const int StderrTailLines = 12;

    public async Task<FfmpegResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct, bool throwOnError = true)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            throw;
        }

        var result = new FfmpegResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        if (throwOnError && result.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(StderrTailLines));
            logger.LogWarning("{Exe} exited {Code}: {Tail}", Path.GetFileName(executable), result.ExitCode, tail);
            throw new FfmpegException($"{Path.GetFileName(executable)} exited with code {result.ExitCode}: {tail.Trim()}");
        }

        return result;
    }
}
