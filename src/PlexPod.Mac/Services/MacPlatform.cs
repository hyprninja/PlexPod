using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PlexPod.App.Services;

namespace PlexPod.Mac.Services;

/// <summary>Avalonia's UI thread behind <see cref="IUiThread"/>.</summary>
public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public async Task InvokeAsync(Action action) => await Dispatcher.UIThread.InvokeAsync(action);
}

/// <summary>The main window, for dialogs that need an owner. Set once the window exists.</summary>
public sealed class WindowLocator
{
    private Window? _main;

    public Window Main
    {
        get => _main ?? throw new InvalidOperationException("The main window isn't open yet.");
        set => _main = value;
    }
}

/// <summary>The macOS folder dialog, and Finder to show a folder.</summary>
public sealed class MacFolderPicker(WindowLocator windows) : IFolderPicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await windows.Main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public void ShowFolder(string folder)
    {
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add(folder);
        Process.Start(start)?.Dispose();
    }
}

/// <summary>Shows the sync plan in a modal sheet over the main window.</summary>
public sealed class SyncConfirmation(WindowLocator windows) : ISyncConfirmation
{
    public async Task<bool> ConfirmAsync(string title, string summary, bool canStart)
    {
        var window = new SyncConfirmWindow(title, summary, canStart);
        return await window.ShowDialog<bool>(windows.Main);
    }
}
