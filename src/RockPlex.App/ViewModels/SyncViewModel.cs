using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RockPlex.App.Services;
using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using RockPlex.Core.Sync;
using RockPlex.Device.Sync;
using RockPlex.Media.Sync;

namespace RockPlex.App.ViewModels;

/// <summary>Shows the plan summary and asks whether to start. Implemented by a WPF dialog.</summary>
public interface ISyncConfirmation
{
    bool Confirm(string title, string summary, bool canStart);
}

/// <summary>
/// The Sync button (M3): preview (read-only) → confirm → execute with progress and a safe Stop.
/// Also reads what RockPlex already owns on the iPod, so the capacity bar counts it as available.
/// </summary>
public sealed partial class SyncViewModel(
    SyncEngine engine,
    LibraryState library,
    SettingsViewModel settings,
    DeviceViewModel device,
    ISyncConfirmation confirmation,
    ILogger<SyncViewModel> logger) : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ResolvedSelection _selection = ResolvedSelection.Empty;
    private SelectionProfile? _profile;

    /// <summary>Raised on the UI thread when a sync finishes (sizes on the device changed).</summary>
    public event EventHandler? Finished;

    /// <summary>Raised when the user chooses to load the selection stored on the iPod.</summary>
    public event EventHandler<SelectionProfile>? LoadDeviceSelectionRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SyncCommand))]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    /// <summary>Bytes in files RockPlex already owns on the connected iPod (from its manifest).</summary>
    [ObservableProperty]
    public partial long ManagedBytes { get; private set; }

    [ObservableProperty]
    public partial string DeviceSelectionHint { get; private set; } = string.Empty;

    private SelectionProfile? DeviceSelection { get; set; }

    public void SetSelection(ResolvedSelection selection, SelectionProfile profile)
    {
        _selection = selection;
        _profile = profile;
        HasSelection = selection.TrackCount > 0 || selection.PlaylistKeys.Count > 0;
        SyncCommand.NotifyCanExecuteChanged(); // also depends on the library and the device
        UpdateDeviceSelectionHint();
    }

    /// <summary>Reads the iPod's manifest and saved selection (off the UI thread). Call when the device changes.</summary>
    public async Task RefreshDeviceAsync()
    {
        SyncCommand.NotifyCanExecuteChanged();
        if (device.SelectedDevice is not { } connected)
        {
            ManagedBytes = 0;
            DeviceSelection = null;
            UpdateDeviceSelectionHint();
            return;
        }

        try
        {
            var store = new DeviceStore(connected.DriveRoot);
            var (manifest, _) = await Task.Run(() => store.LoadManifestAsync(CancellationToken.None));
            ManagedBytes = manifest?.ManagedBytes ?? 0;
            var json = await Task.Run(() => store.TryReadTextAsync(SyncEngine.SelectionFileName, CancellationToken.None));
            DeviceSelection = json is null ? null : JsonSerializer.Deserialize<SelectionProfile>(json, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't read RockPlex state from the iPod: {Message}", ex.Message);
            ManagedBytes = 0;
            DeviceSelection = null;
        }

        UpdateDeviceSelectionHint();
    }

    private bool CanSync() => HasSelection && device.SelectedDevice is not null && library.Snapshot is not null;

    [RelayCommand(CanExecute = nameof(CanSync), IncludeCancelCommand = true)]
    private async Task SyncAsync(CancellationToken stop)
    {
        if (library.Client is not { } client || library.Snapshot is not { } snapshot || device.SelectedDevice is not { } ipod || _profile is null)
        {
            return;
        }

        if (settings.Ffmpeg is null)
        {
            await settings.DetectAsync();
        }

        if (settings.Ffmpeg is not { } ffmpeg)
        {
            ProgressText = "ffmpeg is needed to sync. Open Settings to point RockPlex at it.";
            return;
        }

        ProgressValue = 0;
        ProgressText = "Planning the sync (reading the iPod, comparing with Plex)…";
        var request = new SyncRequest(ipod.DriveRoot, ipod.VolumeLabel, ipod.RockboxTarget, client, ffmpeg, snapshot, _selection,
            library.PlaylistContents, library.FormatOverrides, library.KnownSizes, settings.Policy, _profile);

        SyncPreview preview;
        try
        {
            preview = await Task.Run(() => engine.PreviewAsync(request, stop), stop);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            ProgressText = $"Couldn't plan the sync: {ex.Message}";
            return;
        }

        if (!confirmation.Confirm(preview.Plan.IsEmpty ? "Already in sync" : "Ready to sync", Summarise(preview), preview.CanStart && !preview.Plan.IsEmpty))
        {
            ProgressText = preview.Plan.IsEmpty ? "The iPod already matches your selection." : "Sync cancelled; nothing was changed.";
            return;
        }

        var progress = new Progress<SyncProgress>(p =>
        {
            ProgressValue = p.Total > 0 ? (double)p.Done / p.Total : 0;
            ProgressText = $"{p.Phase}{(p.Total > 0 ? $" {p.Done:N0}/{p.Total:N0}" : string.Empty)}: {p.Current}  ·  {Format.Bytes(p.BytesWritten)} written";
        });

        try
        {
            var result = await Task.Run(() => engine.ExecuteAsync(preview, progress, stop), CancellationToken.None);
            ProgressValue = 1;
            ProgressText = result.Completed
                ? $"Sync complete: {result.Added:N0} added, {result.Retagged + result.Replaced:N0} updated, {result.Removed:N0} removed, {result.PlaylistsWritten:N0} playlists written ({Format.Bytes(result.BytesWritten)}, {result.Elapsed:mm\\:ss}). Run Database → Update Now on the iPod."
                : result.Stopped
                    ? "Sync stopped. The iPod is consistent; press Sync again to finish."
                    : $"Sync finished with {result.Failures.Count:N0} problem(s). Details: /.rockplex/{SyncEngine.ReportFileName} on the iPod.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            logger.LogError(ex, "Sync failed");
            ProgressText = $"Sync failed: {ex.Message}. Nothing is broken on the iPod; sync again to finish.";
        }
        finally
        {
            await RefreshDeviceAsync();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void LoadDeviceSelection()
    {
        if (DeviceSelection is { } profile)
        {
            LoadDeviceSelectionRequested?.Invoke(this, profile);
        }
    }

    private void UpdateDeviceSelectionHint()
    {
        var onDevice = DeviceSelection;
        DeviceSelectionHint = onDevice is null || _profile is null || onDevice.SectionKey != _profile.SectionKey || SameRefs(onDevice, _profile)
            ? string.Empty
            : $"This iPod carries a different saved selection ({Describe(onDevice)}).";
    }

    private static bool SameRefs(SelectionProfile a, SelectionProfile b) =>
        a.AllSongs == b.AllSongs
        && a.Playlists.Select(r => r.RatingKey).Order().SequenceEqual(b.Playlists.Select(r => r.RatingKey).Order())
        && a.Artists.Select(r => r.RatingKey).Order().SequenceEqual(b.Artists.Select(r => r.RatingKey).Order())
        && a.Albums.Select(r => r.RatingKey).Order().SequenceEqual(b.Albums.Select(r => r.RatingKey).Order());

    private static string Describe(SelectionProfile p) =>
        p.AllSongs
            ? "all songs" + (p.Playlists.Count > 0 ? $" + {Format.Count(p.Playlists.Count, "playlist")}" : string.Empty)
            : $"{Format.Count(p.Playlists.Count, "playlist")}, {Format.Count(p.Artists.Count, "artist")}, {Format.Count(p.Albums.Count, "album")}";

    internal static string Summarise(SyncPreview preview)
    {
        var plan = preview.Plan;
        var fit = preview.Fit;
        var addBytes = plan.TrackOps.Where(o => o.Kind is TrackOpKind.Add).Sum(o => o.Target.EstimatedBytes);
        var removeBytes = plan.TrackRemovals.Sum(t => t.SizeBytes);
        var b = new StringBuilder();
        foreach (var blocker in preview.Blockers)
        {
            b.AppendLine("⛔ " + blocker);
        }

        if (preview.Blockers.Count > 0)
        {
            b.AppendLine();
        }

        b.AppendLine($"Add: {Format.Count(plan.Count(TrackOpKind.Add), "track")} (≈ {Format.Bytes(addBytes)})");
        b.AppendLine($"Replace (new file in Plex): {plan.Count(TrackOpKind.Replace):N0}");
        b.AppendLine($"Check / update tags: {plan.Count(TrackOpKind.Retag) + plan.TrackOps.Count(o => o.Kind == TrackOpKind.Move && o.AlsoRetag):N0}");
        b.AppendLine($"Move (renamed in Plex): {plan.Count(TrackOpKind.Move):N0}");
        b.AppendLine($"Remove: {Format.Count(plan.TrackRemovals.Count, "track")} ({Format.Bytes(removeBytes)}), last");
        b.AppendLine($"Playlists to write: {plan.PlaylistOps.Count(p => p.NeedsWrite):N0} of {plan.PlaylistOps.Count:N0} · to remove: {plan.PlaylistRemovals.Count:N0}");
        b.AppendLine($"Unchanged: {Format.Count(plan.Count(TrackOpKind.Keep), "track")}");
        b.AppendLine();
        b.AppendLine($"Free now: {Format.Bytes(fit.FreeBytes)} · free after: ≈ {Format.Bytes(Math.Max(0, fit.FreeAfterSync))}");

        if (preview.Adoption.Tracks.Count + preview.Adoption.Playlists.Count > 0)
        {
            b.AppendLine($"Already on the iPod from an earlier export: {Format.Count(preview.Adoption.Tracks.Count, "track")} and {Format.Count(preview.Adoption.Playlists.Count, "playlist")} will be adopted, not copied again.");
        }

        if (preview.UsesMakeRoom)
        {
            b.AppendLine();
            b.AppendLine("Space is tight, so removals run first (make-room mode). These playlists are briefly absent during the sync, never broken:");
            foreach (var title in preview.BrieflyAbsentPlaylists.Take(10))
            {
                b.AppendLine("  • " + title);
            }
        }

        if (fit.Outcome == FitOutcome.TooBig)
        {
            b.AppendLine();
            b.AppendLine($"Over by {Format.Bytes(fit.OverBy)}. Largest additions:");
            foreach (var op in fit.LargestWrites.Take(5))
            {
                b.AppendLine($"  • {op.Target.Track.Title} ({Format.Bytes(op.Target.EstimatedBytes)})");
            }
        }

        var unavailable = preview.UnavailableEntries.Count;
        if (unavailable > 0)
        {
            b.AppendLine();
            b.AppendLine($"{Format.Count(unavailable, "playlist entry", "playlist entries")} will be skipped: the file is missing on the Plex server.");
        }

        b.AppendLine();
        b.AppendLine("Every file is read back from the iPod and verified. Your own files and playlists are never touched.");
        return b.ToString();
    }
}
