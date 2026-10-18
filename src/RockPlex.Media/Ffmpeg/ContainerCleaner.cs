namespace RockPlex.Media.Ffmpeg;

/// <summary>
/// Rebuilds MP4-family files (M4A, AAC, ALAC) as a fresh container holding only the audio track:
/// packets are copied untouched, every old metadata atom, chapter track and cover-art stream is
/// dropped. TagLib then writes into a clean, standard layout. Some real-world M4As keep their tags
/// where TagLib reads but doesn't rewrite them, so the old tags would otherwise survive.
/// </summary>
public sealed class ContainerCleaner(FfmpegRunner runner)
{
    private static readonly HashSet<string> Mp4Extensions = new(StringComparer.OrdinalIgnoreCase) { ".m4a", ".mp4", ".m4b", ".aac", ".alac" };

    public static bool NeedsRemux(string extension) => Mp4Extensions.Contains(extension);

    /// <summary>
    /// The extension the cleaned file gets: the MP4 output of a raw ADTS <c>.aac</c> or an <c>.alac</c>
    /// is an M4A, which both TagLib and Rockbox recognise (neither knows those two names as MP4).
    /// </summary>
    public static string OutputExtension(string extension) =>
        extension.Equals(".aac", StringComparison.OrdinalIgnoreCase) || extension.Equals(".alac", StringComparison.OrdinalIgnoreCase)
            ? ".m4a"
            : extension;

    public async Task RemuxAsync(FfmpegInstall ffmpeg, string input, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        List<string> args = ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-map", "0:a:0", "-c", "copy"];
        if (input.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
        {
            // Raw ADTS frames carry their config in every header; MP4 needs it once, up front.
            args.AddRange(["-bsf:a", "aac_adtstoasc"]);
        }

        args.AddRange(["-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact", "-movflags", "+faststart", "-f", "mp4", output]);
        await runner.RunAsync(ffmpeg.Path, args, ct).ConfigureAwait(false);
    }
}
