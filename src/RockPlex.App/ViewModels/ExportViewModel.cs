using System.Diagnostics;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RockPlex.App.Services;
using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using RockPlex.Media.Pipeline;

namespace RockPlex.App.ViewModels;

public interface IFolderPicker
{
    string? PickFolder(string title);
}

public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>
/// M2's Test export: processes the current selection into a PC folder (never the iPod) so the
/// pipeline's output can be checked and copied across by hand.
/// </summary>
public sealed partial class ExportViewModel(
    TestExportService exporter,
    LibraryState library,
    SettingsViewModel settings,
    IFolderPicker folders,
    ILogger<ExportViewModel> logger) : ObservableObject
{
    private ResolvedSelection _selection = ResolvedSelection.Empty;

    /// <summary>Raised on the UI thread after an export finishes (new conversions change known sizes).</summary>
    public event EventHandler? Finished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool HasSelection { get; private set; }

    [ObservableProperty]
    public partial double ProgressValue { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    public partial string? LastFolder { get; private set; }

    public void SetSelection(ResolvedSelection selection)
    {
        _selection = selection;
        HasSelection = selection.TrackCount > 0;
    }

    private bool CanExport() => HasSelection;

    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private async Task ExportAsync(CancellationToken ct)
    {
        if (library.Client is not { } client || library.Snapshot is not { } snapshot)
        {
            ProgressText = "Load a library first.";
            return;
        }

        if (settings.Ffmpeg is not { } ffmpeg)
        {
            await settings.DetectAsync();
            if (settings.Ffmpeg is null)
            {
                ProgressText = "ffmpeg is needed for export. Open Settings to point RockPlex at it.";
                return;
            }

            ffmpeg = settings.Ffmpeg;
        }

        var folder = folders.PickFolder("Choose a folder on this PC for the test export");
        if (folder is null)
        {
            return;
        }

        if (TestExportService.IsOnRockboxDrive(folder))
        {
            ProgressText = "That folder is on the iPod. Test export writes to the PC only; copy the files across yourself.";
            return;
        }

        var tracks = ResolveTracks(snapshot, _selection);
        var playlists = _selection.PlaylistKeys
            .Select(k => (Playlist: snapshot.FindPlaylist(k), Contents: library.PlaylistContents.GetValueOrDefault(k)))
            .Where(p => p.Playlist is not null && p.Contents is not null)
            .Select(p => (p.Playlist!, p.Contents!))
            .ToList();

        ProgressValue = 0;
        ProgressText = $"Exporting {Format.Count(tracks.Count, "track")}…";
        var progress = new Progress<ExportProgress>(p =>
        {
            ProgressValue = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            ProgressText = $"Exporting {p.Done:N0}/{p.Total:N0}: {p.Current}";
        });

        try
        {
            var report = await Task.Run(
                () => exporter.ExportAsync(client, ffmpeg, new ExportRequest(folder, snapshot, tracks, playlists, settings.Policy), progress, ct),
                ct);
            LastFolder = folder;
            ProgressValue = 1;
            ProgressText = $"Exported {report.Succeeded:N0} of {report.Succeeded + report.Failed:N0} ({Format.Bytes(report.Bytes)})"
                + (report.Failed > 0 ? $", {report.Failed:N0} failed: see {TestExportService.ReportFileName}" : string.Empty)
                + (report.FromCache > 0 ? $", {report.FromCache:N0} conversions reused" : string.Empty) + ".";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ProgressText = "Export cancelled. Files already finished are complete; nothing half-written was left.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or HttpRequestException)
        {
            logger.LogWarning(ex, "Test export failed");
            ProgressText = $"Export failed: {ex.Message}";
        }
        finally
        {
            settings.UpdateCacheUsage();
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanOpenFolder() => LastFolder is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (LastFolder is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LastFolder}\"") { UseShellExecute = false })?.Dispose();
        }
    }

    /// <summary>Selected tracks from the library, plus playlist tracks that live in another section.</summary>
    private List<TrackItem> ResolveTracks(LibrarySnapshot snapshot, ResolvedSelection selection)
    {
        var fromPlaylists = library.PlaylistContents.Values
            .SelectMany(c => c.Entries)
            .Where(e => e.Track is not null)
            .Select(e => e.Track!)
            .DistinctBy(t => t.RatingKey)
            .ToDictionary(t => t.RatingKey, StringComparer.Ordinal);

        return selection.TrackKeys
            .Select(k => snapshot.FindTrack(k) ?? fromPlaylists.GetValueOrDefault(k))
            .OfType<TrackItem>()
            .ToList();
    }
}
