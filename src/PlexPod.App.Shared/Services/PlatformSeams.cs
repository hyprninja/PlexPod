namespace PlexPod.App.Services;

/// <summary>
/// The UI thread of whichever app hosts the view models (WPF's or Avalonia's dispatcher). Drive and
/// network work never runs here; this is only for handing results back to bound properties.
/// </summary>
public interface IUiThread
{
    /// <summary>Queues <paramref name="action"/> on the UI thread and returns at once.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread and completes when it has run.</summary>
    Task InvokeAsync(Action action);
}

/// <summary>The Plex account token, kept by each platform's own secret store (DPAPI file on Windows, the Keychain on macOS). Never logged.</summary>
public interface ITokenStore
{
    string? Load();

    void Save(string token);

    void Clear();
}

/// <summary>Folder dialogs and showing a folder in Explorer or Finder.</summary>
public interface IFolderPicker
{
    Task<string?> PickFolderAsync(string title);

    void ShowFolder(string folder);
}

/// <summary>Shows the sync plan and asks whether to start.</summary>
public interface ISyncConfirmation
{
    /// <summary>
    /// Shows <paramref name="summary"/> with a Start button when <paramref name="canStart"/>, otherwise
    /// just Close. Completes with true only when the user chose Start.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string summary, bool canStart);
}

/// <summary>Wording that differs between the Windows and Mac apps.</summary>
public static class PlatformText
{
    /// <summary>"PC" on Windows, "Mac" on macOS.</summary>
    public static string ThisComputer => OperatingSystem.IsWindows() ? "PC" : "Mac";

    public static string FfmpegMissing => OperatingSystem.IsWindows()
        ? "ffmpeg not found. Install it (winget install Gyan.FFmpeg) or enter the path to ffmpeg.exe."
        : "ffmpeg not found. Install it with soxr: brew tap homebrew-ffmpeg/ffmpeg, then brew install homebrew-ffmpeg/ffmpeg/ffmpeg --with-libsoxr (Homebrew's plain ffmpeg has no soxr). Or enter the path to ffmpeg.";
}
