using PlexPod.Core.Models;

namespace PlexPod.Core.Media;

/// <summary>What happens to a track's audio on its way to the iPod.</summary>
public abstract record FormatDecision
{
    private FormatDecision()
    {
    }

    /// <summary>Audio copied untouched (tags and art are still rewritten in M2).</summary>
    public sealed record Copy : FormatDecision
    {
        public static readonly Copy Instance = new();
    }

    /// <summary>FLAC converted down. Both targets are always ≤ the source values.</summary>
    public sealed record ConvertFlac(int TargetBits, int TargetRateHz) : FormatDecision;

    /// <summary>FLAC whose bit depth or sample rate is not known yet.</summary>
    public sealed record UnknownFlac : FormatDecision
    {
        public static readonly UnknownFlac Instance = new();
    }
}

/// <summary>
/// The format rule: FLAC above 16-bit/44.1 kHz goes down to min(bits, 16) / min(rate, 44100).
/// Nothing is ever upsampled, and non-FLAC audio is never re-encoded.
/// </summary>
public static class FormatPolicy
{
    public const int MaxBits = 16;
    public const int MaxRateHz = 44_100;

    public static FormatDecision Decide(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (!format.IsFlac)
        {
            return FormatDecision.Copy.Instance;
        }

        if (format.BitDepth is not int bits || bits <= 0 || format.SampleRateHz is not int rate || rate <= 0)
        {
            return FormatDecision.UnknownFlac.Instance;
        }

        var targetBits = Math.Min(bits, MaxBits);
        var targetRate = Math.Min(rate, MaxRateHz);

        return targetBits == bits && targetRate == rate
            ? FormatDecision.Copy.Instance
            : new FormatDecision.ConvertFlac(targetBits, targetRate);
    }
}
