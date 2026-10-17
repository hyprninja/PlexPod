using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RockPlex.Plex.Library;
using RockPlex.Plex.Servers;

namespace RockPlex.App.ViewModels;

/// <summary>Server and library pickers. Raises events only for user-driven changes.</summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private bool _quiet;

    public event EventHandler? ServerPicked;

    public event EventHandler? SectionPicked;

    public ObservableCollection<PlexServer> Servers { get; } = [];

    public ObservableCollection<MusicSection> Sections { get; } = [];

    [ObservableProperty]
    public partial PlexServer? SelectedServer { get; set; }

    [ObservableProperty]
    public partial MusicSection? SelectedSection { get; set; }

    /// <summary>Manual base URL (e.g. http://192.168.1.20:32400). Wins over discovery when reachable.</summary>
    [ObservableProperty]
    public partial string UrlOverride { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConnectionText { get; set; } = string.Empty;

    public Uri? OverrideUri =>
        Uri.TryCreate(UrlOverride.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>Sets lists and selections from code without raising the "picked" events.</summary>
    public void SetServers(IEnumerable<PlexServer> servers, PlexServer? selected) => Quietly(() =>
    {
        Servers.Clear();
        foreach (var s in servers)
        {
            Servers.Add(s);
        }

        SelectedServer = selected;
    });

    public void SetSections(IEnumerable<MusicSection> sections, MusicSection? selected) => Quietly(() =>
    {
        Sections.Clear();
        foreach (var s in sections)
        {
            Sections.Add(s);
        }

        SelectedSection = selected;
    });

    public void Clear()
    {
        SetSections([], null);
        SetServers([], null);
        ConnectionText = string.Empty;
    }

    partial void OnSelectedServerChanged(PlexServer? value)
    {
        if (!_quiet && value is not null)
        {
            ServerPicked?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnSelectedSectionChanged(MusicSection? value)
    {
        if (!_quiet && value is not null)
        {
            SectionPicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Quietly(Action action)
    {
        _quiet = true;
        try
        {
            action();
        }
        finally
        {
            _quiet = false;
        }
    }
}
