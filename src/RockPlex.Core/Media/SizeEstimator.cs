using RockPlex.Core.Models;

namespace RockPlex.Core.Media;

/// <summary>
/// Bytes a track will take on the iPod. <see cref="IsExact"/> is false when the format is unknown
/// and the source size was used as a conservative stand-in (the UI shows "≈").
/// </summary>
public readonly record struct SizeEstimate(long Bytes, bool IsExact, FormatDecision Decision);

public static class SizeEstimator
{
    public static SizeEstimate EstimateBytes(TrackItem track) => EstimateBytes(track, track.Format);

    /// <summary>Estimates with <paramref name="format"/> in place of the track's own (e.g. enriched details).</summary>
    public static SizeEstimate EstimateBytes(TrackItem track, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(format);

        var size = track.SourceSizeBytes;
        var decision = FormatPolicy.Decide(format);

        switch (decision)
        {
            case FormatDecision.ConvertFlac convert:
                // ConvertFlac guarantees both source values are known and positive.
                var ratio = (double)convert.TargetBits / format.BitDepth!.Value
                          * convert.TargetRateHz / format.SampleRateHz!.Value;
                // The ratio is ≤ 1, so the estimate never exceeds the source.
                var bytes = Math.Min(size, (long)Math.Ceiling(size * ratio));
                return new SizeEstimate(bytes, IsExact: true, decision);

            case FormatDecision.UnknownFlac:
                return new SizeEstimate(size, IsExact: false, decision);

            default:
                return new SizeEstimate(size, IsExact: true, decision);
        }
    }

    /// <summary>Total for a set of tracks (unavailable ones skipped); exact only if every estimate is.</summary>
    public static (long Bytes, bool IsExact) EstimateTotal(
        IEnumerable<TrackItem> tracks,
        IReadOnlyDictionary<string, AudioFormat>? formatOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        long total = 0;
        var exact = true;
        foreach (var track in tracks)
        {
            if (!track.IsAvailable)
            {
                continue;
            }

            var format = formatOverrides?.GetValueOrDefault(track.RatingKey) ?? track.Format;
            var estimate = EstimateBytes(track, format);
            total += estimate.Bytes;
            exact &= estimate.IsExact;
        }

        return (total, exact);
    }
}
