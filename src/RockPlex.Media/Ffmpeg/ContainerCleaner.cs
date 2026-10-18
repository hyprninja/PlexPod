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

    public async Task RemuxAsync(FfmpegInstall ffmpeg, string input, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ffmpeg);
        await runner.RunAsync(ffmpeg.Path,
        [
            "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input,
            "-map", "0:a:0", "-c", "copy", "-map_metadata", "-1", "-map_chapters", "-1",
            "-fflags", "+bitexact", "-movflags", "+faststart", "-f", "mp4", output,
        ], ct).ConfigureAwait(false);
    }
}
