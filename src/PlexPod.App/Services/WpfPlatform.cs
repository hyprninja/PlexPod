using System.Diagnostics;
using System.Windows;
using PlexPod.App.Services;

namespace PlexPod.App.Services;

/// <summary>The WPF dispatcher behind <see cref="IUiThread"/>.</summary>
public sealed class WpfUiThread : IUiThread
{
    public void Post(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) => Application.Current.Dispatcher.InvokeAsync(action).Task;
}

/// <summary>The Windows folder dialog, and Explorer to show a folder.</summary>
public sealed class FolderPicker : IFolderPicker
{
    public Task<string?> PickFolderAsync(string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public void ShowFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose();
}
