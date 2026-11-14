using System.Diagnostics;

namespace PlexPod.App.Services;

public interface IBrowserLauncher
{
    void Open(string url);
}

public sealed class BrowserLauncher : IBrowserLauncher
{
    public void Open(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
