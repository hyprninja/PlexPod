using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using RockPlex.App.Services;
using RockPlex.Core.Selection;

namespace RockPlex.App.ViewModels;

/// <summary>The capacity bar and the counts under it.</summary>
public sealed partial class SummaryViewModel : ObservableObject
{
    /// <summary>Used for the bar when no iPod is connected (about 73 GiB usable on the 80 GB model).</summary>
    public const long NominalUsableBytes = 73L * 1024 * 1024 * 1024;

    /// <summary>0–1 for the bar.</summary>
    [ObservableProperty]
    public partial double FillValue { get; private set; }

    [ObservableProperty]
    public partial bool IsOver { get; private set; }

    [ObservableProperty]
    public partial string BarText { get; private set; } = "Nothing selected";

    [ObservableProperty]
    public partial string BreakdownText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WarningText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCalculating { get; set; }

    public void Update(ResolvedSelection selection, CapacityResult capacity, bool hasDevice)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(capacity);

        FillValue = Math.Clamp(capacity.FillRatio, 0, 1);
        IsOver = !capacity.Fits;

        var approx = selection.IsEstimateExact ? string.Empty : " ≈";
        var of = hasDevice ? $"of {Format.Bytes(capacity.AvailableBytes)} free" : $"of ~{Format.Bytes(capacity.AvailableBytes)} (no iPod connected)";
        var bar = new StringBuilder($"{Format.Bytes(selection.TotalEstimatedBytes)}{approx} {of}  ·  {Format.Count(selection.TrackCount, "track")}");
        if (IsOver)
        {
            bar.Append($"  ·  over by {Format.Bytes(capacity.OverBy)}");
        }

        BarText = bar.ToString();

        var c = selection.Counts;
        var parts = new List<string>();
        if (c.FromPlaylists > 0)
        {
            parts.Add($"playlists {c.FromPlaylists:N0}");
        }

        if (c.FromArtists > 0)
        {
            parts.Add($"artists {c.FromArtists:N0}");
        }

        if (c.FromAlbums > 0)
        {
            parts.Add($"albums {c.FromAlbums:N0}");
        }

        if (c.FromAllSongs > 0)
        {
            parts.Add($"all songs {c.FromAllSongs:N0}");
        }

        if (selection.ConversionCount > 0)
        {
            parts.Add($"{selection.ConversionCount:N0} to convert");
        }

        if (selection.UnknownFormatCount > 0)
        {
            parts.Add($"{selection.UnknownFormatCount:N0} format unknown (sized conservatively)");
        }

        var unavailable = selection.UnavailablePlaylistEntries.Count + selection.UnavailableTrackCount;
        if (unavailable > 0)
        {
            parts.Add($"{unavailable:N0} unavailable");
        }

        BreakdownText = string.Join(" · ", parts);

        var warnings = new List<string>();
        if (selection.PendingPlaylistKeys.Count > 0)
        {
            warnings.Add($"Loading {Format.Count(selection.PendingPlaylistKeys.Count, "playlist")}…");
        }

        if (selection.MissingRefs.Count > 0)
        {
            var names = string.Join(", ", selection.MissingRefs.Take(5).Select(m => $"{m.Kind.ToString().ToLowerInvariant()} \"{m.Title}\""));
            var more = selection.MissingRefs.Count > 5 ? $" and {selection.MissingRefs.Count - 5} more" : string.Empty;
            warnings.Add($"No longer in Plex: {names}{more}.");
        }

        if (selection.UnavailablePlaylistEntries.Count > 0)
        {
            var first = selection.UnavailablePlaylistEntries[0];
            warnings.Add($"{Format.Count(selection.UnavailablePlaylistEntries.Count, "playlist entry", "playlist entries")} can't be copied (file missing on the server), e.g. \"{first.TrackTitle ?? "unknown"}\" in \"{first.PlaylistTitle}\".");
        }

        WarningText = string.Join("  ", warnings);
    }

    public void Clear()
    {
        FillValue = 0;
        IsOver = false;
        BarText = "Nothing selected";
        BreakdownText = string.Empty;
        WarningText = string.Empty;
    }
}
