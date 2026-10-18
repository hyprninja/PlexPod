using System.IO;

namespace PlexPod.App.Services;

/// <summary>
/// Everything PlexPod keeps on the PC lives under <c>%LOCALAPPDATA%\PlexPod\</c>. Data from before the
/// rename (<c>%LOCALAPPDATA%\RockPlex\</c>) is moved there on first start.
/// </summary>
public sealed class AppPaths
{
    public const string FolderName = "PlexPod";

    /// <summary>The app's name before the rebrand.</summary>
    public const string LegacyFolderName = "RockPlex";

    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName))
    {
    }

    public AppPaths(string root)
    {
        Root = root;
        MigrationNote = MigrateLegacy(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
    }

    public string Root { get; }

    /// <summary>What happened to the pre-rename data folder on this start, for the log; null if nothing.</summary>
    public string? MigrationNote { get; }

    public string Settings => Path.Combine(Root, "settings.json");

    public string Token => Path.Combine(Root, "token.bin");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>Media pipeline caches (work files, converted audio, album art).</summary>
    public string Cache => Path.Combine(Root, "cache");

    /// <summary>
    /// Moves <c>RockPlex\</c> (beside <paramref name="root"/>) to <paramref name="root"/> when only the old one
    /// exists. If the move is blocked (a file in use), the settings, token and play history are copied
    /// instead; the caches are rebuilt as needed.
    /// </summary>
    internal static string? MigrateLegacy(string root)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(root));
        if (parent is null || Directory.Exists(root))
        {
            return null;
        }

        var legacy = Path.Combine(parent, LegacyFolderName);
        if (!Directory.Exists(legacy))
        {
            return null;
        }

        try
        {
            Directory.Move(legacy, root);
            return $"Moved {legacy} to {root} (renamed app).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(root);
            foreach (var file in new[] { "settings.json", "token.bin" })
            {
                var from = Path.Combine(legacy, file);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(root, file));
                }
            }

            CopyTree(Path.Combine(legacy, "stats"), Path.Combine(root, "stats"));
            return $"Couldn't move {legacy} ({ex.Message}); copied its settings, token and play history to {root}.";
        }
    }

    private static void CopyTree(string from, string to)
    {
        if (!Directory.Exists(from))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
