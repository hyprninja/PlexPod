using System.Globalization;
using PlexPod.Core.Media;

namespace PlexPod.Media.Ffmpeg;

/// <summary>
/// Runs the pipeline's FLAC conversions (pipeline §3.2): soxr resampling at very high precision,
/// TPDF dither on the step down to 16-bit, 4096-sample frames, and no source tags or pictures.
/// </summary>
public sealed class FlacConverter(FfmpegRunner runner)
{
    public async Task ConvertAsync(FfmpegInstall ffmpeg, string input, string output, FlacStreamInfo source, FlacAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        var args = BuildArguments(ffmpeg, input, output, source, action);
        await runner.RunAsync(ffmpeg.Path, args, ct).ConfigureAwait(false);
    }

    /// <exception cref="NotSupportedException">The action can't be done without raising a value.</exception>
    public static IReadOnlyList<string> BuildArguments(FfmpegInstall ffmpeg, string input, string output, FlacStreamInfo source, FlacAction action)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);

        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-map", "0:a:0", "-map_metadata", "-1"];
        switch (action)
        {
            case FlacAction.Convert convert:
                // ffmpeg's FLAC encoder writes 16- or 24/32-bit only, so a sub-16-bit source would come out
                // with more bits than it went in. That breaks the never-upsample rule, so it is refused.
                if (convert.TargetBits != 16)
                {
                    throw new NotSupportedException($"Converting a {source.BitsPerSample}-bit source would raise its bit depth; it is reported instead.");
                }

                // soxr is needed only to change the sample rate; a bit-depth-only step (24/44.1 → 16/44.1) dithers without it.
                var resample = convert.TargetRateHz < source.SampleRateHz;
                if (resample && !ffmpeg.HasSoxr)
                {
                    throw new NotSupportedException("This ffmpeg build has no soxr resampler, so FLAC above 44.1 kHz can't be converted.");
                }

                var filter = resample
                    ? string.Create(CultureInfo.InvariantCulture, $"aresample=osr={convert.TargetRateHz}:osf=s16:resampler=soxr:precision=28:dither_method=triangular")
                    : "aresample=osf=s16:dither_method=triangular";
                args.AddRange(["-af", filter, "-sample_fmt", "s16"]);
                break;

            case FlacAction.ReencodeLossless:
                break;

            default:
                throw new ArgumentException("Copies don't run ffmpeg.", nameof(action));
        }

        args.AddRange(["-c:a", "flac", "-compression_level", "8",
            "-frame_size", FinalFormatPolicy.OutputBlockSize.ToString(CultureInfo.InvariantCulture), "-f", "flac", output]);
        return args;
    }
}
