using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using RockPlex.App.Services;
using RockPlex.Core.Media;
using RockPlex.Core.Models;
using RockPlex.Core.Selection;

namespace RockPlex.App.ViewModels;

/// <summary>All songs toggle plus the Playlists / Artists / Albums pickers.</summary>
public sealed partial class SelectionViewModel : ObservableObject
{
    // Saved picks not found in the current library; kept so a temporarily missing item isn't forgotten.
    private List<ItemRef> _missingPlaylists = [];
    private List<ItemRef> _missingArtists = [];
    private List<ItemRef> _missingAlbums = [];
    private bool _restoring;

    public SelectionViewModel()
    {
        Playlists.Changed += (_, _) => RaiseChanged();
        Artists.Changed += (_, _) => RaiseChanged();
        Albums.Changed += (_, _) => RaiseChanged();
    }

    public event EventHandler? Changed;

    public PickerListViewModel Playlists { get; } = new("Playlists");

    public PickerListViewModel Artists { get; } = new("Artists");

    public PickerListViewModel Albums { get; } = new("Albums");

    [ObservableProperty]
    public partial bool AllSongs { get; set; }

    [ObservableProperty]
    public partial bool HasLibrary { get; private set; }

    public bool HasMissingRefs => _missingPlaylists.Count + _missingArtists.Count + _missingAlbums.Count > 0;

    /// <summary>Row data is computed off the UI thread; the result is applied with <see cref="Apply"/>.</summary>
    public static PickerRows BuildRows(LibrarySnapshot snapshot)
    {
        var artists = snapshot.Artists
            .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(a =>
            {
                var tracks = snapshot.TracksByArtist(a.RatingKey).ToList();
                var albums = tracks.Select(t => t.AlbumKey).Distinct(StringComparer.Ordinal).Count();
                var row = new PickerItemViewModel(a.RatingKey, a.Guid, a.Title, a.Title)
                {
                    Detail = $"{Format.Count(albums, "album")} · {Format.Count(tracks.Count, "track")}",
                };
                SetSize(row, tracks);
                return row;
            })
            .ToList();

        var albumRows = snapshot.Albums
            .OrderBy(a => a.ArtistTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Year ?? 0)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(a =>
            {
                var tracks = snapshot.TracksByAlbum(a.RatingKey).ToList();
                var year = a.Year is int y ? string.Create(CultureInfo.InvariantCulture, $" ({y})") : string.Empty;
                var title = $"{a.ArtistTitle} — {a.Title}{year}";
                var row = new PickerItemViewModel(a.RatingKey, a.Guid, title, title)
                {
                    Detail = Format.Count(tracks.Count, "track"),
                    Badge = tracks.Any(t => FormatPolicy.Decide(t.Format) is FormatDecision.ConvertFlac) ? "hi-res → 16/44.1" : string.Empty,
                };
                SetSize(row, tracks);
                return row;
            })
            .ToList();

        var playlists = snapshot.Playlists
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PickerItemViewModel(p.RatingKey, p.Guid, p.Title, p.Title)
            {
                Detail = Format.Count(p.LeafCount, "track"),
                Badge = p.IsSmart ? "smart" : string.Empty,
                SizeText = "…",
            })
            .ToList();

        return new PickerRows(playlists, artists, albumRows);
    }

    public void Apply(PickerRows rows, SelectionProfile? saved, LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _restoring = true;
        try
        {
            Playlists.SetItems(rows.Playlists);
            Artists.SetItems(rows.Artists);
            Albums.SetItems(rows.Albums);
            HasLibrary = true;
            Restore(saved, snapshot);
        }
        finally
        {
            _restoring = false;
        }

        RaiseChanged();
    }

    public void ClearLibrary()
    {
        HasLibrary = false;
        Playlists.SetItems([]);
        Artists.SetItems([]);
        Albums.SetItems([]);
    }

    /// <summary>Fills in a playlist row's size once its items have loaded.</summary>
    public void UpdatePlaylistRow(PlaylistContents contents, LibrarySnapshot snapshot)
    {
        var row = Playlists.Items.FirstOrDefault(r => r.Key == contents.PlaylistRatingKey);
        if (row is null)
        {
            return;
        }

        var tracks = contents.Entries
            .Where(e => !e.Unavailable && e.Track is not null)
            .Select(e => snapshot.FindTrack(e.Track!.RatingKey) ?? e.Track!)
            .DistinctBy(t => t.RatingKey)
            .ToList();
        var unavailable = contents.Entries.Count(e => e.Unavailable);
        row.Detail = Format.Count(contents.Entries.Count, "track") + (unavailable > 0 ? $" · {unavailable} unavailable" : string.Empty);
        SetSize(row, tracks);
    }

    public SelectionProfile BuildProfile(string sectionKey) =>
        new(
            sectionKey,
            AllSongs,
            [.. Playlists.Checked.Select(i => i.ToRef()), .. _missingPlaylists],
            [.. Artists.Checked.Select(i => i.ToRef()), .. _missingArtists],
            [.. Albums.Checked.Select(i => i.ToRef()), .. _missingAlbums]);

    /// <summary>Replaces the current picks with <paramref name="profile"/> (e.g. the selection stored on the iPod).</summary>
    public void ApplyProfile(SelectionProfile profile, LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _restoring = true;
        try
        {
            Restore(profile, snapshot);
        }
        finally
        {
            _restoring = false;
        }

        RaiseChanged();
    }

    /// <summary>Drops saved picks that no longer exist in Plex.</summary>
    public void ForgetMissing()
    {
        _missingPlaylists = [];
        _missingArtists = [];
        _missingAlbums = [];
        OnPropertyChanged(nameof(HasMissingRefs));
        RaiseChanged();
    }

    partial void OnAllSongsChanged(bool value)
    {
        Artists.IsEnabled = !value;
        Albums.IsEnabled = !value;
        RaiseChanged();
    }

    private void Restore(SelectionProfile? saved, LibrarySnapshot snapshot)
    {
        if (saved is null || saved.SectionKey != snapshot.SectionKey)
        {
            AllSongs = false;
            Playlists.SetChecked(new HashSet<string>());
            Artists.SetChecked(new HashSet<string>());
            Albums.SetChecked(new HashSet<string>());
            _missingPlaylists = _missingArtists = _missingAlbums = [];
            OnPropertyChanged(nameof(HasMissingRefs));
            return;
        }

        AllSongs = saved.AllSongs;
        _missingPlaylists = RestoreList(Playlists, saved.Playlists, RefKind.Playlist, snapshot.FindPlaylist, snapshot.Playlists, p => p.Guid, p => p.RatingKey);
        _missingArtists = RestoreList(Artists, saved.Artists, RefKind.Artist, snapshot.FindArtist, snapshot.Artists, a => a.Guid, a => a.RatingKey);
        _missingAlbums = RestoreList(Albums, saved.Albums, RefKind.Album, snapshot.FindAlbum, snapshot.Albums, a => a.Guid, a => a.RatingKey);
        OnPropertyChanged(nameof(HasMissingRefs));
    }

    /// <summary>Re-matches refs by rating key, then guid (same rules as the resolver); returns the unmatched ones.</summary>
    private static List<ItemRef> RestoreList<T>(
        PickerListViewModel list,
        IReadOnlyList<ItemRef> refs,
        RefKind kind,
        Func<string, T?> find,
        IReadOnlyList<T> all,
        Func<T, string> guidOf,
        Func<T, string> keyOf)
        where T : class
    {
        var missing = new List<MissingRef>();
        var found = SelectionResolver.ResolveRefs(refs, kind, find, all, guidOf, missing);
        list.SetChecked(found.Select(keyOf).ToHashSet(StringComparer.Ordinal));
        return missing.Select(m => new ItemRef(m.RatingKey, m.Guid, m.Title)).ToList();
    }

    private static void SetSize(PickerItemViewModel row, IEnumerable<TrackItem> tracks)
    {
        var (bytes, exact) = SizeEstimator.EstimateTotal(tracks);
        row.SizeText = (exact ? string.Empty : "≈ ") + Format.Bytes(bytes);
    }

    private void RaiseChanged()
    {
        if (!_restoring)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed record PickerRows(
    IReadOnlyList<PickerItemViewModel> Playlists,
    IReadOnlyList<PickerItemViewModel> Artists,
    IReadOnlyList<PickerItemViewModel> Albums);
