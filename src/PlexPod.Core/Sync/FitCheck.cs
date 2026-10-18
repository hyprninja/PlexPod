using PlexPod.Core.Selection;

namespace PlexPod.Core.Sync;

public enum FitOutcome
{
    /// <summary>Everything fits in the current free space (removals still run last).</summary>
    Fits,

    /// <summary>Fits only once removals have run: make-room mode (D5).</summary>
    FitsAfterRemovals,

    /// <summary>Doesn't fit even after removals: blocked before anything is written.</summary>
    TooBig,
}

/// <param name="BytesNeeded">Peak extra space while copying: adds, growth of replaced files, plus room for one temp file.</param>
/// <param name="BytesFreed">Space released by removals.</param>
public sealed record FitResult(
    FitOutcome Outcome,
    long FreeBytes,
    long UsableBytes,
    long BytesNeeded,
    long BytesFreed,
    long OverBy,
    IReadOnlyList<TrackOp> LargestWrites)
{
    public long FreeAfterSync => UsableBytes - BytesNeeded + BytesFreed;
}

/// <summary>The gate before anything is written (M3 plan §4). Conservative: estimates never under.</summary>
public static class FitCheck
{
    /// <param name="reserveBytes">Extra room needed besides tracks and playlists (the stock firmware's database and its temp copy).</param>
    public static FitResult Evaluate(SyncPlan plan, long freeBytes, long reserveBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);

        long added = 0, growth = 0, largestOverwrite = 0;
        var writes = new List<TrackOp>();
        foreach (var op in plan.TrackOps)
        {
            switch (op.Kind)
            {
                case TrackOpKind.Add:
                    added += op.Target.EstimatedBytes;
                    writes.Add(op);
                    break;

                case TrackOpKind.Replace or TrackOpKind.Retag:
                case TrackOpKind.Move when op.AlsoRetag:
                    var old = op.Existing?.SizeBytes ?? 0;
                    var replacedInPlace = op.Existing is not null && string.Equals(op.Existing.DevicePath, op.DevicePath, StringComparison.Ordinal);

                    // A file written to a new path keeps the old one until removals; in place, only growth counts,
                    // but the temp copy sits beside the original until the rename.
                    growth += replacedInPlace || op.Kind == TrackOpKind.Move ? Math.Max(0, op.Target.EstimatedBytes - old) : op.Target.EstimatedBytes;
                    largestOverwrite = Math.Max(largestOverwrite, Math.Min(old, op.Target.EstimatedBytes));
                    writes.Add(op);
                    break;
            }
        }

        var needed = added + growth + largestOverwrite + PlaylistBytes(plan) + Math.Max(0, reserveBytes);
        var freed = plan.TrackRemovals.Sum(t => t.SizeBytes);
        var usable = Math.Max(0, freeBytes - CapacityCalculator.ReserveBytes);
        var outcome = needed <= usable ? FitOutcome.Fits
            : needed <= usable + freed ? FitOutcome.FitsAfterRemovals
            : FitOutcome.TooBig;
        var over = Math.Max(0, needed - usable - freed);
        var largest = writes.OrderByDescending(o => o.Target.EstimatedBytes).Take(10).ToList();
        return new FitResult(outcome, freeBytes, usable, needed, freed, over, largest);
    }

    private static long PlaylistBytes(SyncPlan plan) =>
        plan.PlaylistOps.Where(p => p.NeedsWrite).Sum(p => (long)System.Text.Encoding.UTF8.GetByteCount(p.Content) * 2);
}
