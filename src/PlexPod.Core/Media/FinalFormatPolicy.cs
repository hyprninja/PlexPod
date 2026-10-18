using PlexPod.Core.Models;

namespace PlexPod.Core.Media;

/// <summary>What the pipeline does to a downloaded FLAC, decided from its real STREAMINFO.</summary>
public abstract record FlacAction
{
    private FlacAction()
    {
    }

    /// <summary>Audio copied bit-for-bit; only tags and pictures change.</summary>
    public sealed record Copy : FlacAction
    {
        public static readonly Copy Instance = new();
    }

    /// <summary>Converted down (resample and/or dithered bit reduction). Targets are never above the source.</summary>
    public sealed record Convert(int TargetBits, int TargetRateHz) : FlacAction;

    /// <summary>FLAC → FLAC with 4096-sample frames; the audio stays bit-identical.</summary>
    public sealed record ReencodeLossless : FlacAction
    {
        public static readonly ReencodeLossless Instance = new();
    }
}

/// <summary>
/// The format rule applied to the file itself (pipeline §3.2), plus the block-size safety net:
/// a file that would be copied but has blocks larger than Rockbox's decoder limit on this target
/// is re-encoded losslessly with smaller frames.
/// </summary>
public static class FinalFormatPolicy
{
    /// <summary>⚠️ Rockbox's FLAC MAX_BLOCKSIZE on less capable targets such as the iPod Video.</summary>
    public const int MaxSafeBlockSize = 4608;

    /// <summary>Frame size used for every FLAC the pipeline encodes.</summary>
    public const int OutputBlockSize = 4096;

    public static FlacAction Decide(FlacStreamInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        // Same rule as the estimate (FormatPolicy), so the plan and the real output always agree.
        var decision = FormatPolicy.Decide(new AudioFormat("flac", "flac", null, info.BitsPerSample, info.SampleRateHz, info.Channels));
        return decision switch
        {
            FormatDecision.ConvertFlac c => new FlacAction.Convert(c.TargetBits, c.TargetRateHz),
            _ when info.MaxBlockSize > MaxSafeBlockSize => FlacAction.ReencodeLossless.Instance,
            _ => FlacAction.Copy.Instance,
        };
    }
}
