using System.IO;

namespace RockPlex.App.Services;

/// <summary>Everything RockPlex keeps on the PC lives under <c>%LOCALAPPDATA%\RockPlex\</c>.</summary>
public sealed class AppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RockPlex"))
    {
    }

    public AppPaths(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    public string Settings => Path.Combine(Root, "settings.json");

    public string Token => Path.Combine(Root, "token.bin");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Media pipeline caches (work files, converted audio, album art).</summary>
    public string Cache => Path.Combine(Root, "cache");
}
