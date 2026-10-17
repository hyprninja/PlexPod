using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RockPlex.App.Services;
using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using RockPlex.Plex.Library;
using RockPlex.Plex.Servers;

namespace RockPlex.App.ViewModels;

/// <summary>
/// Drives the M1 flow: sign-in → servers → connection → music section → library → pickers, with the
/// capacity bar recalculated (debounced) on every change. Read-only against Plex and the iPod.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int PlaylistLoadConcurrency = 4;
    private const int EnrichBatchSize = 50;

    private readonly IPlexServerDiscovery _discovery;
    private readonly IPlexLibraryClientFactory _clients;
    private readonly TrackDetailEnricher _enricher;
    private readonly SettingsStore _settings;
    private readonly LibraryState _library;
    private readonly ILogger<MainViewModel> _logger;
    private readonly Debouncer _recalc = new(TimeSpan.FromMilliseconds(150));
    private readonly HashSet<string> _enrichAttempted = new(StringComparer.Ordinal);

    private CancellationTokenSource _loadCts = new();
    private CancellationTokenSource? _enrichCts;
    private bool _initialized;

    public MainViewModel(
        SignInViewModel signIn,
        ServerViewModel server,
        DeviceViewModel device,
        SelectionViewModel selection,
        SummaryViewModel summary,
        IPlexServerDiscovery discovery,
        IPlexLibraryClientFactory clients,
        TrackDetailEnricher enricher,
        SettingsStore settings,
        LibraryState library,
        ILogger<MainViewModel> logger)
    {
        SignIn = signIn;
        Server = server;
        Device = device;
        Selection = selection;
        Summary = summary;
        _discovery = discovery;
        _clients = clients;
        _enricher = enricher;
        _settings = settings;
        _library = library;
        _logger = logger;

        Server.UrlOverride = settings.Current.ServerUrlOverride ?? string.Empty;

        SignIn.SignedIn += async (_, _) => await RunLoadAsync(LoadServersAsync);
        SignIn.SignedOut += (_, _) => OnSignedOut();
        Server.ServerPicked += async (_, _) => await RunLoadAsync(ct => ConnectAsync(Server.SelectedServer!, ct));
        Server.SectionPicked += async (_, _) => await RunLoadAsync(ct => LoadLibraryAsync(Server.SelectedSection!, ct));
        Selection.Changed += (_, _) => ScheduleRecalc();
        Device.CapacityChanged += (_, _) => ScheduleRecalc();
    }

    public SignInViewModel SignIn { get; }

    public ServerViewModel Server { get; }

    public DeviceViewModel Device { get; }

    public SelectionViewModel Selection { get; }

    public SummaryViewModel Summary { get; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Device.Start();
        ScheduleRecalc();
        await RunLoadAsync(async ct =>
        {
            StatusText = "Checking your Plex sign-in…";
            if (await SignIn.TryRestoreAsync(ct))
            {
                await LoadServersAsync(ct);
            }
            else
            {
                StatusText = "Sign in with Plex to get started.";
            }
        });
    }

    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
        _enrichCts?.Cancel();
        _enrichCts?.Dispose();
        _recalc.Dispose();
        Device.Dispose();
    }

    [RelayCommand]
    private Task RefreshAsync() =>
        RunLoadAsync(async ct =>
        {
            if (!SignIn.IsSignedIn && !await SignIn.TryRestoreAsync(ct))
            {
                StatusText = "Sign in with Plex to get started.";
                return;
            }

            await LoadServersAsync(ct);
        });

    /// <summary>Saves the manual server URL and reconnects with it.</summary>
    [RelayCommand]
    private async Task ApplyServerUrlAsync()
    {
        var text = Server.UrlOverride.Trim();
        if (text.Length > 0 && Server.OverrideUri is null)
        {
            StatusText = "The server URL must look like http://192.168.1.20:32400";
            return;
        }

        await _settings.UpdateAsync(s => s with { ServerUrlOverride = text.Length > 0 ? text : null }, CancellationToken.None);
        if (Server.SelectedServer is { } server)
        {
            await RunLoadAsync(ct => ConnectAsync(server, ct));
        }
    }

    [RelayCommand]
    private void ForgetMissing() => Selection.ForgetMissing();

    /// <summary>Cancels any load in progress, then runs <paramref name="load"/> with error reporting.</summary>
    private async Task RunLoadAsync(Func<CancellationToken, Task> load)
    {
        await _loadCts.CancelAsync();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsBusy = true;
        try
        {
            await load(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by a newer load.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Plex.Http.PlexProtocolException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Plex request failed");
            StatusText = ex is TaskCanceledException
                ? "Plex didn't answer in time. Press Refresh to retry."
                : $"Plex request failed: {ex.Message}. Press Refresh to retry.";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadServersAsync(CancellationToken ct)
    {
        StatusText = "Finding your Plex servers…";
        var servers = await _discovery.GetServersAsync(ct);
        var saved = _settings.Current.ServerMachineId;
        var pick = servers.FirstOrDefault(s => s.MachineIdentifier == saved)
            ?? (servers.Count == 1 ? servers[0] : servers.FirstOrDefault(s => s.Owned));
        Server.SetServers(servers, pick);

        if (servers.Count == 0)
        {
            StatusText = "No Plex Media Server found on this account.";
            return;
        }

        if (pick is null)
        {
            StatusText = "Choose a server.";
            return;
        }

        await ConnectAsync(pick, ct);
    }

    private async Task ConnectAsync(PlexServer server, CancellationToken ct)
    {
        ClearLibrary();
        StatusText = $"Connecting to {server.Name}…";
        Server.ConnectionText = string.Empty;
        var connection = await _discovery.SelectConnectionAsync(server, Server.OverrideUri, ct);
        if (connection is null)
        {
            StatusText = $"Couldn't reach {server.Name} on your network. Check it's running, or enter its address manually.";
            return;
        }

        Server.ConnectionText = connection.BaseUri.GetLeftPart(UriPartial.Authority);
        await _settings.UpdateAsync(s => s with { ServerMachineId = server.MachineIdentifier }, ct);

        var client = _clients.Create(connection);
        _library.Reset(client, null);
        var sections = await client.GetMusicSectionsAsync(ct);
        var saved = _settings.Current.SectionKey;
        var pick = sections.FirstOrDefault(s => s.Key == saved) ?? sections.FirstOrDefault();
        Server.SetSections(sections, pick);
        if (pick is null)
        {
            StatusText = $"{server.Name} has no music libraries.";
            return;
        }

        await LoadLibraryAsync(pick, ct);
    }

    private async Task LoadLibraryAsync(MusicSection section, CancellationToken ct)
    {
        var client = _library.Client ?? throw new InvalidOperationException("Not connected.");
        ClearLibrary();
        _library.Reset(client, null);

        StatusText = $"Loading artists from {section.Title}…";
        var artists = await client.GetArtistsAsync(section.Key, Progress("artists"), ct);
        StatusText = "Loading albums…";
        var albums = await client.GetAlbumsAsync(section.Key, Progress("albums"), ct);
        StatusText = "Loading tracks…";
        var tracks = await client.GetTracksAsync(section.Key, Progress("tracks"), ct);
        StatusText = "Loading playlists…";
        var playlists = await client.GetPlaylistsAsync(ct);

        var snapshot = new LibrarySnapshot(section.Key, artists, albums, tracks, playlists);
        var rows = await Task.Run(() => SelectionViewModel.BuildRows(snapshot), ct);
        ct.ThrowIfCancellationRequested();

        _library.Reset(client, snapshot);
        _enrichAttempted.Clear();
        await _settings.UpdateAsync(s => s with { SectionKey = section.Key }, ct);
        Selection.Apply(rows, _settings.Current.Selection, snapshot);
        StatusText = $"{section.Title}: {Format.Count(artists.Count, "artist")}, {Format.Count(albums.Count, "album")}, {Format.Count(tracks.Count, "track")}, {Format.Count(playlists.Count, "playlist")}.";
        ScheduleRecalc();

        await LoadPlaylistContentsAsync(client, snapshot, ct);
    }

    /// <summary>Loads every playlist's items so rows show sizes and selected playlists resolve fully.</summary>
    private async Task LoadPlaylistContentsAsync(IPlexLibraryClient client, LibrarySnapshot snapshot, CancellationToken ct)
    {
        var selectedFirst = snapshot.Playlists
            .OrderByDescending(p => Selection.Playlists.Checked.Any(c => c.Key == p.RatingKey))
            .ToList();
        var dispatcher = Application.Current.Dispatcher;
        var contentsMap = _library.PlaylistContents;

        await Parallel.ForEachAsync(
            selectedFirst,
            new ParallelOptions { MaxDegreeOfParallelism = PlaylistLoadConcurrency, CancellationToken = ct },
            async (playlist, token) =>
            {
                try
                {
                    var contents = await client.GetPlaylistContentsAsync(playlist, token);
                    contentsMap[playlist.RatingKey] = contents;
                    await dispatcher.InvokeAsync(() =>
                    {
                        Selection.UpdatePlaylistRow(contents, snapshot);
                        ScheduleRecalc();
                    });
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Playlist {Title} failed to load", playlist.Title);
                }
            });
    }

    private void ClearLibrary()
    {
        _enrichCts?.Cancel();
        _library.Reset(_library.Client, null);
        Selection.ClearLibrary();
        ScheduleRecalc();
    }

    private void OnSignedOut()
    {
        _ = _loadCts.CancelAsync();
        ClearLibrary();
        _library.Reset(null, null);
        Server.Clear();
        IsBusy = false;
        StatusText = "Signed out.";
    }

    private Progress<int> Progress(string what)
    {
        var baseText = StatusText;
        return new Progress<int>(n => StatusText = $"{baseText} {n:N0} {what}");
    }

    private void ScheduleRecalc() => _recalc.Schedule(RecalculateAsync);

    /// <summary>Resolves the selection on a background task, updates the bar, saves the selection, and starts enrichment.</summary>
    private async Task RecalculateAsync(CancellationToken ct)
    {
        var snapshot = _library.Snapshot;
        var capacity = Device.Capacity;
        var hasDevice = capacity is not null;
        capacity ??= new DeviceCapacity(SummaryViewModel.NominalUsableBytes, SummaryViewModel.NominalUsableBytes + CapacityCalculator.ReserveBytes);

        if (snapshot is null || !Selection.HasLibrary)
        {
            Summary.Update(ResolvedSelection.Empty, CapacityCalculator.Calculate(capacity, 0), hasDevice);
            return;
        }

        var profile = Selection.BuildProfile(snapshot.SectionKey);
        var contents = _library.PlaylistContents;
        var overrides = _library.FormatOverrides;

        Summary.IsCalculating = true;
        try
        {
            var resolved = await Task.Run(() => SelectionResolver.Resolve(snapshot, profile, contents, overrides), ct);
            ct.ThrowIfCancellationRequested();
            Summary.Update(resolved, CapacityCalculator.Calculate(capacity, resolved), hasDevice);

            if (!SameProfile(_settings.Current.Selection, profile))
            {
                await _settings.UpdateAsync(s => s with { Selection = profile }, ct);
            }

            StartEnrichment(snapshot, resolved);
        }
        finally
        {
            Summary.IsCalculating = false;
        }
    }

    private static bool SameProfile(SelectionProfile? a, SelectionProfile b) =>
        a is not null
        && a.SectionKey == b.SectionKey
        && a.AllSongs == b.AllSongs
        && a.Playlists.SequenceEqual(b.Playlists)
        && a.Artists.SequenceEqual(b.Artists)
        && a.Albums.SequenceEqual(b.Albums);

    /// <summary>Fetches bit depth / sample rate for selected FLAC tracks whose format is unknown.</summary>
    private void StartEnrichment(LibrarySnapshot snapshot, ResolvedSelection resolved)
    {
        var client = _library.Client;
        var todo = resolved.UnknownFormatTrackKeys
            .Where(_enrichAttempted.Add)
            .Select(snapshot.FindTrack)
            .OfType<TrackItem>()
            .ToList();
        if (client is null || todo.Count == 0)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_loadCts.Token);
        var overrides = _library.FormatOverrides;
        var dispatcher = Application.Current.Dispatcher;
        _enrichCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var batch in todo.Chunk(EnrichBatchSize))
                {
                    await _enricher.EnrichAsync(client, batch, (key, format) => overrides[key] = format, cts.Token);
                    await dispatcher.InvokeAsync(ScheduleRecalc);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
