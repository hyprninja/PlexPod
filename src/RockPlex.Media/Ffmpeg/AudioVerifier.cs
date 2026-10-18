using System.Text.RegularExpressions;
using RockPlex.Core.Media;

namespace RockPlex.Media.Ffmpeg;

/// <summary>The output failed a check; nothing from it is kept.</summary>
public sealed class VerificationException(string message) : Exception(message);

/// <summary>
/// Proves each output is what the plan promised (M2 plan §4.4): untouched audio for copies,
/// identical decoded audio for lossless re-encodes, and no raised values for conversions.
/// </summary>
public sealed partial class AudioVerifier(FfmpegRunner runner)
{
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromMilliseconds(50);

    /// <summary>Copies: the demuxed audio packets must be byte-identical (format-independent).</summary>
    public async Task VerifyCopyAsync(FfmpegInstall ffmpeg, string source, string output, CancellationToken ct)
    {
        var before = await PacketMd5Async(ffmpeg, source, ct).ConfigureAwait(false);
        var after = await PacketMd5Async(ffmpeg, output, ct).ConfigureAwait(false);
        if (before != after)
        {
            throw new VerificationException("The audio changed while re-tagging (packet checksum differs).");
        }

        if (FlacFile.TryReadStreamInfo(source) is { HasAudioMd5: true } src && FlacFile.TryReadStreamInfo(output) is { } dst && src.AudioMd5 != dst.AudioMd5)
        {
            throw new VerificationException("FLAC STREAMINFO MD5 differs from the source.");
        }
    }

    /// <summary>Lossless re-encodes: the decoded samples must be identical and the frames small enough.</summary>
    public async Task VerifyLosslessAsync(FfmpegInstall ffmpeg, string source, string output, CancellationToken ct)
    {
        var dst = FlacFile.ReadStreamInfo(output);
        if (dst.MaxBlockSize > FinalFormatPolicy.MaxSafeBlockSize)
        {
            throw new VerificationException($"Re-encoded file still has {dst.MaxBlockSize}-sample blocks.");
        }

        var before = await DecodedMd5Async(ffmpeg, source, ct).ConfigureAwait(false);
        var after = await DecodedMd5Async(ffmpeg, output, ct).ConfigureAwait(false);
        if (before != after)
        {
            throw new VerificationException("Lossless re-encode changed the audio (decoded checksum differs).");
        }
    }

    /// <summary>Conversions: hit the targets, never exceed the source, decode cleanly, same length.</summary>
    public async Task VerifyConversionAsync(FfmpegInstall ffmpeg, FlacStreamInfo source, string output, FlacAction.Convert target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var dst = FlacFile.ReadStreamInfo(output);

        if (dst.BitsPerSample > source.BitsPerSample || dst.SampleRateHz > source.SampleRateHz)
        {
            throw new VerificationException($"Output {dst.BitsPerSample}/{dst.SampleRateHz} is higher than the source {source.BitsPerSample}/{source.SampleRateHz}.");
        }

        if (dst.BitsPerSample != target.TargetBits || dst.SampleRateHz != target.TargetRateHz)
        {
            throw new VerificationException($"Output is {dst.BitsPerSample}/{dst.SampleRateHz}; expected {target.TargetBits}/{target.TargetRateHz}.");
        }

        if (dst.MaxBlockSize > FinalFormatPolicy.MaxSafeBlockSize)
        {
            throw new VerificationException($"Output has {dst.MaxBlockSize}-sample blocks.");
        }

        // A source may legally report 0 total samples (length unknown); the decode check still runs.
        if (source.TotalSamples > 0 && (dst.Duration - source.Duration).Duration() > DurationTolerance)
        {
            throw new VerificationException($"Output length {dst.Duration} differs from the source {source.Duration}.");
        }

        var check = await runner.RunAsync(ffmpeg.Path, ["-hide_banner", "-nostdin", "-v", "error", "-i", output, "-map", "0:a:0", "-f", "null", "-"], ct, throwOnError: false).ConfigureAwait(false);
        if (check.ExitCode != 0 || !string.IsNullOrWhiteSpace(check.StandardError))
        {
            throw new VerificationException($"Converted file doesn't decode cleanly: {check.StandardError.Trim()}");
        }
    }

    public Task<string> PacketMd5Async(FfmpegInstall ffmpeg, string path, CancellationToken ct) =>
        Md5Async(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-c", "copy", "-f", "md5", "-"], ct);

    public Task<string> DecodedMd5Async(FfmpegInstall ffmpeg, string path, CancellationToken ct) =>
        Md5Async(ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", path, "-map", "0:a:0", "-f", "md5", "-"], ct);

    private async Task<string> Md5Async(FfmpegInstall ffmpeg, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var result = await runner.RunAsync(ffmpeg.Path, args, ct).ConfigureAwait(false);
        return Md5Pattern().Match(result.StandardOutput) is { Success: true } m
            ? m.Groups[1].Value
            : throw new VerificationException("ffmpeg returned no checksum.");
    }

    [GeneratedRegex("MD5=([0-9a-f]{32})")]
    private static partial Regex Md5Pattern();
}
