using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.Core.ItunesDb;
using PlexPod.Core.Models;
using PlexPod.Core.Selection;
using PlexPod.Core.Stats;
using PlexPod.Core.Sync;
using PlexPod.Core.Tagging;
using PlexPod.Device.Sync;
using PlexPod.Media;
using PlexPod.Media.Art;
using PlexPod.Media.Ffmpeg;
using PlexPod.Media.Pipeline;
using PlexPod.Media.Stats;
using PlexPod.Media.Sync;
using PlexPod.Media.Tags;

namespace PlexPod.Tests.Media;

/// <summary>
/// End-to-end syncs: fake Plex library, the real M2 pipeline (ffmpeg), and a temp folder as the iPod.
/// Every test checks that the user's own files survive and that every playlist line resolves.
/// </summary>
public sealed class SyncEngineTests : IDisposable
{
    private const long Plenty = 50L << 30;

    private readonly TempDir _dir = new();
    private readonly FakeLibraryClient _client = new() { Image = TagWriterTests.Jpeg(600, 600) };
    private readonly string _ipod;
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty("3");
    private Dictionary<string, PlaylistContents> _contents = [];

    public SyncEngineTests()
    {
        _ipod = _dir.File("ipod");
        Directory.CreateDirectory(Path.Combine(_ipod, ".rockbox"));
        Plant("/Music/User Stuff/my own.mp3", "the user's file");
        Plant("/Playlists/Mine.m3u8", "#EXTM3U\n/Music/User Stuff/my own.mp3\n");
    }

    public void Dispose() => _dir.Dispose();

    private string Local(string devicePath) => DevicePaths.ToLocal(_ipod, devicePath);

    private void Plant(string devicePath, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(devicePath))!);
        File.WriteAllText(Local(devicePath), text);
    }

    private TrackItem Track(string key, long part, string album, string ext, int index, string title, long updatedAt = 1) =>
        new(key, $"plex://track/{key}", title, album, album == "alB" ? "arB" : "arA", null, index, 1, 1000,
            new AudioFormat(ext, ext), [new MediaPartRef(part, $"/library/parts/{part}/1/file.{ext}", new FileInfo(_client.PartFiles[part]).Length, ext)], updatedAt);

    /// <summary>Album A: two CD-quality FLACs. Album B: one MP3. Playlist p1: B, A1, B again, plus an unavailable entry.</summary>
    private async Task<(TrackItem A1, TrackItem A2, TrackItem B1)> LibraryAsync()
    {
        _client.PartFiles[1] = await TestFfmpeg.MakeFlacAsync(_dir.File("a1.flac"), 44_100, 16, 0.5);
        _client.PartFiles[2] = await TestFfmpeg.MakeFlacAsync(_dir.File("a2.flac"), 44_100, 16, 0.5);
        _client.PartFiles[3] = _dir.CopyFixture("tone.mp3");
        _client.Albums["alA"] = new AlbumDetail("alA", "Takk...", "arA", "Sigur Rós", 2005, "2005-09-12", ["Post-Rock"], [], null, 2, "/thumb/a");
        _client.Albums["alB"] = new AlbumDetail("alB", "Blue", "arB", "Joni", 1971, null, ["Folk"], [], null, 1, "/thumb/b");

        var a1 = Track("t1", 1, "alA", "flac", 1, "Glósóli");
        var a2 = Track("t2", 2, "alA", "flac", 2, "Hoppípolla");
        var b1 = Track("t3", 3, "alB", "mp3", 1, "River");
        SetLibrary([a1, a2, b1]);
        _contents["p1"] = new PlaylistContents("p1", [new(10, b1, false), new(11, a1, false), new(12, b1, false), new(13, null, true)]);
        return (a1, a2, b1);
    }

    private void SetLibrary(IReadOnlyList<TrackItem> tracks) =>
        _snapshot = new LibrarySnapshot("3",
            [new ArtistItem("arA", "g", "Sigur Rós"), new ArtistItem("arB", "g", "Joni")],
            [new AlbumItem("alA", "g", "Takk...", "arA", "Sigur Rós", 2005, 2, "/thumb/a", 1), new AlbumItem("alB", "g", "Blue", "arB", "Joni", 1971, 1, "/thumb/b", 1)],
            tracks,
            [new PlaylistItem("p1", "g", "Road Trip", false, 4, 0, "/playlists/p1/items")]);

    private SyncEngine Engine(long free = Plenty, Func<string, string>? fingerprint = null)
    {
        var paths = new MediaPaths(_dir.File("cache"));
        var runner = TestFfmpeg.Runner;
        var pipeline = new TrackPipeline(paths, new ConversionCache(paths), new FlacConverter(runner), new ContainerCleaner(runner), new AudioVerifier(runner), NullLogger<TrackPipeline>.Instance);
        return new SyncEngine(pipeline, new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance), paths, new DeviceFileOps(),
            new StatsService(paths, NullLogger<StatsService>.Instance) { Zone = TimeZoneInfo.Utc }, NullLogger<SyncEngine>.Instance)
        {
            FreeSpace = _ => free,
            Fingerprint = fingerprint ?? (_ => "test-ipod"),
        };
    }

    private async Task<SyncRequest> RequestAsync(SelectionProfile profile, TagPolicy? policy = null, StatsOptions? stats = null, PlexPod.Device.IpodFirmware firmware = PlexPod.Device.IpodFirmware.Rockbox)
    {
        var resolved = SelectionResolver.Resolve(_snapshot, profile, _contents);
        return new SyncRequest(_ipod, "IPOD", "ipodvideo", _client, await TestFfmpeg.InstallAsync(), _snapshot, resolved, _contents,
            new Dictionary<string, AudioFormat>(), new Dictionary<string, long>(), policy ?? TagPolicy.Default, profile, stats, firmware);
    }

    private static SelectionProfile Select(bool albumA = true, bool albumB = false, bool playlist = false) =>
        new("3", false,
            playlist ? [new ItemRef("p1", "g", "Road Trip")] : [],
            [],
            [.. albumA ? [new ItemRef("alA", "g", "Takk...")] : Array.Empty<ItemRef>(), .. albumB ? [new ItemRef("alB", "g", "Blue")] : Array.Empty<ItemRef>()]);

    private async Task<SyncResult> SyncAsync(SelectionProfile profile, long free = Plenty, TagPolicy? policy = null, CancellationToken stop = default, StatsOptions? stats = null,
        PlexPod.Device.IpodFirmware firmware = PlexPod.Device.IpodFirmware.Rockbox)
    {
        var engine = Engine(free);
        var preview = await engine.PreviewAsync(await RequestAsync(profile, policy, stats, firmware), CancellationToken.None);
        return await engine.ExecuteAsync(preview, null, stop);
    }

    // ---------------------------------------------------------------- M6: stock firmware

    private const PlexPod.Device.IpodFirmware Stock = PlexPod.Device.IpodFirmware.Stock;

    private Task<SyncResult> StockSyncAsync(SelectionProfile profile, long free = Plenty, StatsOptions? stats = null) =>
        SyncAsync(profile, free, stats: stats, firmware: Stock);

    private ItdbReadResult Database() => ItunesDbReader.Read(File.ReadAllBytes(Local(PlexPod.Device.Sync.DevicePaths.ItunesDbPath)));

    private void MakeStock()
    {
        Directory.Delete(Path.Combine(_ipod, ".rockbox"), recursive: true);
        Directory.CreateDirectory(Path.Combine(_ipod, "iPod_Control", "iTunes"));
        Directory.CreateDirectory(Path.Combine(_ipod, "iPod_Control", "Device"));
        Directory.CreateDirectory(Path.Combine(_ipod, "iPod_Control", "Music"));
    }

    private void AssertStockLibraryIsSound()
    {
        var db = Database();
        foreach (var track in db.Tracks)
        {
            Assert.True(File.Exists(Local(IpodPath.FromColon(track.Track.IpodPath))), $"{track.Track.Title} points at a missing file");
        }

        Assert.True(db.Playlists[0].IsMaster);
        Assert.Equal(db.Tracks.Count, db.Playlists[0].TrackIds.Count);
    }

    [FfmpegFact]
    public async Task Stock_FirstSync_WritesAppleLosslessAndTheDatabase()
    {
        MakeStock();
        var (a1, _, b1) = await LibraryAsync();

        var result = await StockSyncAsync(Select(albumA: true, playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(3, result.Added);
        var db = Database();
        Assert.Equal(3, db.Tracks.Count);
        var byTitle = db.Tracks.ToDictionary(t => t.Track.Title);
        Assert.Equal(ItdbFileKind.AppleLossless, byTitle["Glósóli"].Track.Kind);
        Assert.Equal(ItdbFileKind.Mp3, byTitle["River"].Track.Kind);
        Assert.Equal("Sigur Rós", byTitle["Glósóli"].Track.Artist);
        Assert.Equal("Takk...", byTitle["Glósóli"].Track.Album);
        Assert.Equal(1u, byTitle["Glósóli"].Track.TrackNumber);
        Assert.Equal(2005u, byTitle["Glósóli"].Track.Year);
        Assert.Equal(44_100u, byTitle["Glósóli"].Track.SampleRate);
        Assert.All(db.Tracks, t => Assert.Matches(@"^:iPod_Control:Music:F\d\d:[A-Z]{4}\.(m4a|mp3)$", t.Track.IpodPath));

        var road = Assert.Single(db.Playlists, p => !p.IsMaster);
        Assert.Equal("Road Trip", road.Name);
        Assert.Equal([byTitle["River"].Id, byTitle["Glósóli"].Id, byTitle["River"].Id], road.TrackIds);
        Assert.Equal("IPOD", db.Playlists[0].Name);
        Assert.Equal(5, db.Playlists[0].SortIndexCount);

        Assert.False(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        Assert.False(Directory.Exists(Local("/Music/Sigur Rós")));
        Assert.Empty(Directory.GetFiles(_ipod, "cover.jpg", SearchOption.AllDirectories));
        var manifest = await ManifestAsync();
        Assert.Equal(DeviceManifest.FirmwareStock, manifest.Firmware);
        Assert.All(manifest.Tracks, t => Assert.NotNull(t.Stock));
        Assert.Contains("iPod library rebuilt: 3 tracks, 1 playlists", result.Report, StringComparison.Ordinal);
        AssertStockLibraryIsSound();
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task Stock_Covers_GoIntoTheArtworkDbAndThumbnailFiles()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, albumB: true));

        var db = Database();
        Assert.All(db.Tracks, t => Assert.True(t.Track.ArtworkId >= ArtworkDb.FirstImageId));
        var art = ArtworkDb.Read(File.ReadAllBytes(Local("/iPod_Control/Artwork/ArtworkDB")));
        Assert.Equal(db.Tracks.Select(t => (t.Track.ArtworkId, t.Track.Dbid)).Order(), art.Select(a => (a.ImageId, a.SongDbid)).Order());
        Assert.Equal(3 * 100 * 100 * 2, new FileInfo(Local("/iPod_Control/Artwork/F1028_1.ithmb")).Length);
        Assert.Equal(3 * 200 * 200 * 2, new FileInfo(Local("/iPod_Control/Artwork/F1029_1.ithmb")).Length);
        Assert.All(art, a => Assert.Equal(["F1028_1.ithmb", "F1029_1.ithmb"], a.Thumbs.Select(t => t.FileName)));

        // Unchanged covers: nothing rewritten. New art in Plex: the other index, and the old one goes.
        var written = File.GetLastWriteTimeUtc(Local("/iPod_Control/Artwork/F1029_1.ithmb"));
        SetLibrary([.. _snapshot.Tracks.Select(t => t with { UpdatedAt = 5 })]);
        await StockSyncAsync(Select(albumA: true, albumB: true));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Local("/iPod_Control/Artwork/F1029_1.ithmb")));

        _client.Image = TagWriterTests.Jpeg(400, 400, fill: 0x20); // a visibly different cover
        _client.Albums["alA"] = _client.Albums["alA"] with { ThumbPath = "/thumb/a-new" };
        _snapshot = new LibrarySnapshot("3", _snapshot.Artists,
            [.. _snapshot.Albums.Select(a => a.RatingKey == "alA" ? a with { ThumbPath = "/thumb/a-new" } : a)],
            [.. _snapshot.Tracks.Select(t => t with { UpdatedAt = 6 })],
            _snapshot.Playlists);
        var result = await StockSyncAsync(Select(albumA: true, albumB: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.True(File.Exists(Local("/iPod_Control/Artwork/F1029_2.ithmb")));
        Assert.False(File.Exists(Local("/iPod_Control/Artwork/F1029_1.ithmb")));
        Assert.All(ArtworkDb.Read(File.ReadAllBytes(Local("/iPod_Control/Artwork/ArtworkDB"))), a => Assert.Contains("_2.ithmb", a.Thumbs[1].FileName, StringComparison.Ordinal));
        AssertStockLibraryIsSound();
    }

    [FfmpegFact]
    public async Task Stock_ResyncWithNoChanges_TouchesNothing()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, playlist: true));
        var before = File.ReadAllBytes(Local(PlexPod.Device.Sync.DevicePaths.ItunesDbPath));
        var downloads = _client.Downloads;

        var preview = await Engine().PreviewAsync(await RequestAsync(Select(albumA: true, playlist: true), firmware: Stock), CancellationToken.None);

        Assert.False(preview.HasWork);
        Assert.Equal(before, File.ReadAllBytes(Local(PlexPod.Device.Sync.DevicePaths.ItunesDbPath)));
        Assert.Equal(downloads, _client.Downloads);
    }

    [FfmpegFact]
    public async Task Stock_PlaylistChangeInPlex_RebuildsTheDatabaseOnly()
    {
        MakeStock();
        var (a1, a2, b1) = await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, playlist: true));
        var downloads = _client.Downloads;

        _contents["p1"] = new PlaylistContents("p1", [new(10, a2, false), new(11, a1, false), new(12, b1, false)]);
        var preview = await Engine().PreviewAsync(await RequestAsync(Select(albumA: true, playlist: true), firmware: Stock), CancellationToken.None);
        Assert.True(preview.Plan.IsEmpty);
        Assert.True(preview.HasWork);
        var result = await Engine().ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(downloads, _client.Downloads);
        var db = Database();
        var titles = db.Tracks.ToDictionary(t => t.Id, t => t.Track.Title);
        Assert.Equal(["Hoppípolla", "Glósóli", "River"], Assert.Single(db.Playlists, p => !p.IsMaster).TrackIds.Select(id => titles[id]));
    }

    [FfmpegFact]
    public async Task Stock_RemovedAlbum_LeavesTheDatabaseFirstThenTheFiles()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, albumB: true));
        var removedFiles = (await ManifestAsync()).Tracks.Where(t => t.Stock!.Album == "Takk...").Select(t => t.DevicePath).ToList();

        var result = await StockSyncAsync(Select(albumA: false, albumB: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(2, result.Removed);
        Assert.All(removedFiles, p => Assert.False(File.Exists(Local(p))));
        Assert.Equal(["River"], Database().Tracks.Select(t => t.Track.Title));
        AssertStockLibraryIsSound();
    }

    [FfmpegFact]
    public async Task Stock_MakeRoom_WritesAnInterimDatabaseBeforeRemoving()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, albumB: false));
        var used = (await ManifestAsync()).ManagedBytes;

        // Only room for the new album once the old one is gone.
        var request = await RequestAsync(Select(albumA: false, albumB: true), firmware: Stock);
        var roomy = (await Engine().PreviewAsync(request, CancellationToken.None)).Fit;
        var tight = CapacityCalculator.ReserveBytes + roomy.BytesNeeded - (roomy.BytesFreed / 2);
        var preview = await Engine(free: tight).PreviewAsync(request, CancellationToken.None);
        Assert.True(preview.UsesMakeRoom, preview.Fit.ToString());
        var result = await Engine().ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(["River"], Database().Tracks.Select(t => t.Track.Title));
        AssertStockLibraryIsSound();
        Assert.True(used > 0);
    }

    [FfmpegFact]
    public async Task Stock_PlayCounts_ReachPlex_AreKeptOnTheIpod_AndTheFileIsDeleted()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, playlist: true));
        var db = Database();
        var entries = db.Tracks.Select(t => t.Track.Title switch
        {
            "Glósóli" => new PlayCountEntry(2, new DateTime(2026, 10, 18, 8, 15, 0), 0, 80, 0, null),
            "River" => new PlayCountEntry(0, null, 0, 0, 1, new DateTime(2026, 10, 18, 9, 0, 0)),
            _ => new PlayCountEntry(0, null, 0, 0, 0, null),
        }).ToList();
        File.WriteAllBytes(Local(PlexPod.Device.Sync.DevicePaths.PlayCountsPath), PlayCountsReader.Write(entries));

        var preview = await Engine().PreviewAsync(await RequestAsync(Select(albumA: true, playlist: true), stats: StatsOn, firmware: Stock), CancellationToken.None);
        Assert.Equal((2, 1), preview.StockStats);
        var result = await Engine().ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(["play:t1", "play:t1", "rate:t1=8"], _client.Writes);
        Assert.False(File.Exists(Local(PlexPod.Device.Sync.DevicePaths.PlayCountsPath)));
        var glosoli = Database().Tracks.Single(t => t.Track.Title == "Glósóli").Track;
        Assert.Equal(2u, glosoli.PlayCount);
        Assert.Equal(80, glosoli.Rating);
        Assert.Equal(new DateTime(2026, 10, 18, 8, 15, 0), glosoli.LastPlayed);
        Assert.Equal(1u, Database().Tracks.Single(t => t.Track.Title == "River").Track.SkipCount);
        Assert.Contains("Sent 2 plays and 1 rating", result.Report, StringComparison.Ordinal);

        await StockSyncAsync(Select(albumA: true, playlist: true), stats: StatsOn);
        Assert.Equal(3, _client.Writes.Count); // nothing sent twice
        Assert.Equal(2u, Database().Tracks.Single(t => t.Track.Title == "Glósóli").Track.PlayCount);
    }

    [FfmpegFact]
    public async Task Stock_PlexDown_PlaysWaitAndAreSentNextTime()
    {
        MakeStock();
        await LibraryAsync();
        await StockSyncAsync(Select(albumA: true));
        var db = Database();
        File.WriteAllBytes(Local(PlexPod.Device.Sync.DevicePaths.PlayCountsPath),
            PlayCountsReader.Write([.. db.Tracks.Select(_ => new PlayCountEntry(1, new DateTime(2026, 10, 18, 8, 0, 0), 0, 0, 0, null))]));

        _client.FailWrite = _ => true;
        var down = await StockSyncAsync(Select(albumA: true), stats: StatsOn);
        Assert.True(down.Completed, string.Join("\n", down.Failures));
        Assert.Empty(_client.Writes);
        Assert.Contains("Couldn't reach Plex. 2 plays will be sent next time.", down.Report, StringComparison.Ordinal);
        Assert.False(File.Exists(Local(PlexPod.Device.Sync.DevicePaths.PlayCountsPath)));

        _client.FailWrite = null;
        await StockSyncAsync(Select(albumA: true), stats: StatsOn);
        Assert.Equal(["play:t1", "play:t2"], _client.Writes.Order());
    }

    [FfmpegFact]
    public async Task Stock_AfterRockbox_OldLibraryAndPlaylistFilesAreCleanedUp()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, playlist: true)); // Rockbox era: /Music, /Playlists, cover.jpg
        Assert.True(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        MakeStock();

        var result = await StockSyncAsync(Select(albumA: true, playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.False(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        Assert.False(Directory.Exists(Local("/Music/Sigur Rós")));
        Assert.False(Directory.Exists(Local("/Music/Joni")));
        Assert.Equal(3, Database().Tracks.Count);
        Assert.Contains(result.Warnings, w => w.Contains("wrote for Rockbox will be removed", StringComparison.Ordinal));
        AssertStockLibraryIsSound();
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task Stock_TrackThatFails_KeepsThePlaylistsPreviousVersion()
    {
        MakeStock();
        var (a1, a2, b1) = await LibraryAsync();
        await StockSyncAsync(Select(albumA: true, playlist: true));
        var before = Assert.Single(Database().Playlists, p => !p.IsMaster).TrackIds.Count;

        // Plex adds a new track to the playlist, and it fails to download.
        _client.PartFiles[5] = _client.PartFiles[2];
        var added = Track("t5", 5, "alA", "flac", 3, "Sé Lest");
        SetLibrary([a1, a2, b1, added]);
        _client.ExplodeOnDownload.Add(5);
        _contents["p1"] = new PlaylistContents("p1", [new(10, b1, false), new(11, a1, false), new(14, added, false)]);

        var result = await StockSyncAsync(Select(albumA: true, playlist: true));

        Assert.False(result.Completed);
        Assert.Contains(result.Failures, f => f.Contains("The previous version stays", StringComparison.Ordinal));
        Assert.Equal(before, Assert.Single(Database().Playlists, p => !p.IsMaster).TrackIds.Count);
        AssertStockLibraryIsSound();
    }

    // ---------------------------------------------------------------- rebrand

    private async Task<DeviceManifest> ManifestAsync() => (await new DeviceStore(_ipod).LoadManifestAsync(CancellationToken.None)).Manifest!;

    private void AssertUserFilesUntouched()
    {
        Assert.Equal("the user's file", File.ReadAllText(Local("/Music/User Stuff/my own.mp3")));
        Assert.Equal("#EXTM3U\n/Music/User Stuff/my own.mp3\n", File.ReadAllText(Local("/Playlists/Mine.m3u8")));
    }

    private void AssertEveryPlaylistResolves(DeviceManifest manifest) =>
        Assert.Empty(new PlaylistVerifier().Verify(_ipod, manifest.Playlists.Select(p => p.DevicePath)));

    [FfmpegFact]
    public async Task FirstSync_WritesTracksPlaylistCoverAndState()
    {
        await LibraryAsync();

        var result = await SyncAsync(Select(albumA: true, playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(3, result.Added);
        Assert.Equal(1, result.PlaylistsWritten);
        Assert.True(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac")));
        Assert.True(File.Exists(Local("/Music/Joni/1971 - Blue/01 River.mp3")));
        Assert.True(ArtProcessor.IsBaselineJpeg(File.ReadAllBytes(Local("/Music/Sigur Rós/2005 - Takk/cover.jpg"))));

        var lines = PlaylistWriter.TrackLines(File.ReadAllText(Local("/Playlists/Road Trip.m3u8"))).Select(l => l.Path).ToList();
        Assert.Equal(["/Music/Joni/1971 - Blue/01 River.mp3", "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac", "/Music/Joni/1971 - Blue/01 River.mp3"], lines);
        Assert.Contains(result.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));

        var manifest = await ManifestAsync();
        Assert.Equal(3, manifest.Tracks.Count);
        Assert.All(manifest.Tracks, t => Assert.Equal(64, t.Sha256.Length));
        Assert.True(File.Exists(Path.Combine(_ipod, ".plexpod", "device.json")));
        Assert.True(File.Exists(Path.Combine(_ipod, ".plexpod", SyncEngine.SelectionFileName)));
        Assert.Contains("Added 3", File.ReadAllText(Path.Combine(_ipod, ".plexpod", SyncEngine.ReportFileName)), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_ipod, "*" + DevicePaths.TempSuffix, SearchOption.AllDirectories));
        AssertEveryPlaylistResolves(manifest);
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task SecondSync_WithNoChanges_DoesNothing()
    {
        await LibraryAsync();
        await SyncAsync(Select(playlist: true));
        var downloads = _client.Downloads;

        var engine = Engine();
        var preview = await engine.PreviewAsync(await RequestAsync(Select(playlist: true)), CancellationToken.None);
        Assert.True(preview.Plan.IsEmpty);
        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(0, result.BytesWritten);
        Assert.Equal(downloads, _client.Downloads);
    }

    [FfmpegFact]
    public async Task PlexTitleEdit_MovesAndRetagsOnlyThatTrack()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());
        var downloads = _client.Downloads;

        SetLibrary([a1 with { Title = "Glósóli (Remastered)", UpdatedAt = 2 }, a2, b1]);
        var result = await SyncAsync(Select());

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(1, result.Moved);
        Assert.Equal(1, result.Retagged);
        Assert.Equal(downloads + 1, _client.Downloads);
        Assert.False(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac")));
        var renamed = Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli (Remastered).flac");
        Assert.Contains(TagWriter.ReadBack(renamed).Fields, f => f is { Key: "TITLE", Value: "Glósóli (Remastered)" });
    }

    [FfmpegFact]
    public async Task StampChangeWithoutTagChange_TouchesNothing()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());
        var downloads = _client.Downloads;

        SetLibrary([a1 with { UpdatedAt = 99 }, a2, b1]);
        var result = await SyncAsync(Select());

        Assert.Equal(1, result.TagsAlreadyCurrent);
        Assert.Equal(0, result.BytesWritten);
        Assert.Equal(downloads, _client.Downloads);
        Assert.True((await Engine().PreviewAsync(await RequestAsync(Select()), CancellationToken.None)).Plan.IsEmpty);
    }

    [FfmpegFact]
    public async Task TagPolicyChange_RewritesTheTags()
    {
        await LibraryAsync();
        await SyncAsync(Select());

        var result = await SyncAsync(Select(), policy: TagPolicy.Default with { Genre = false });

        Assert.Equal(2, result.Retagged);
        Assert.DoesNotContain(TagWriter.ReadBack(Local("/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac")).Fields, f => f.Key == "GENRE");
    }

    [FfmpegFact]
    public async Task DeselectingAnAlbum_RemovesItsFilesCoverAndFolders_Last()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: true));

        var result = await SyncAsync(Select(albumA: false, albumB: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(2, result.Removed);
        Assert.False(Directory.Exists(Local("/Music/Sigur Rós")));
        Assert.True(File.Exists(Local("/Music/Joni/1971 - Blue/01 River.mp3")));
        Assert.Single((await ManifestAsync()).Tracks);
        Assert.True(Directory.Exists(Local("/Music")));
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task DeselectingAPlaylist_RemovesItsFile_AndTracksNobodyNeeds()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, playlist: true));

        var result = await SyncAsync(Select(albumA: true, playlist: false));

        Assert.False(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        Assert.Equal(1, result.Removed); // River was only there for the playlist
        Assert.True(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac")));
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task TooBig_IsBlocked_AndNothingIsWritten()
    {
        await LibraryAsync();
        var engine = Engine(free: CapacityCalculator.ReserveBytes + 1000);
        var preview = await engine.PreviewAsync(await RequestAsync(Select(playlist: true)), CancellationToken.None);

        Assert.False(preview.CanStart);
        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.False(Directory.Exists(Local("/Music/Sigur Rós")));
        Assert.False(File.Exists(Path.Combine(_ipod, ".plexpod", "manifest.json")));
        Assert.Equal(0, _client.Downloads);
    }

    [FfmpegFact]
    public async Task MakeRoom_SwapsAlbumsThatOnlyFitAfterRemovals()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: false, playlist: false));
        var used = (await ManifestAsync()).ManagedBytes;

        // Album B fits only once album A is gone: usable space sits between B's size and B's size minus A's.
        var needB = new FileInfo(_client.PartFiles[3]).Length + SyncEngine.TagAllowanceBytes;
        var free = CapacityCalculator.ReserveBytes + needB - (used / 2);
        var engine = Engine(free);
        var preview = await engine.PreviewAsync(await RequestAsync(Select(albumA: false, albumB: true)), CancellationToken.None);
        Assert.True(preview.UsesMakeRoom, $"fit: {preview.Fit}");

        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(2, result.Removed);
        Assert.Equal(1, result.Added);
        Assert.True(used > 0);
        AssertEveryPlaylistResolves(await ManifestAsync());
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task FailedTrack_KeepsThePlaylistsPreviousVersion_AndTheNextSyncFinishes()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select(albumA: false, playlist: true));
        var before = File.ReadAllText(Local("/Playlists/Road Trip.m3u8"));

        // The playlist gains a track whose download fails this time.
        var a2File = _client.PartFiles[2];
        _client.PartFiles[2] = _dir.File("does-not-exist.flac");
        _contents["p1"] = new PlaylistContents("p1", [.. _contents["p1"].Entries, new(14, a2, false)]);
        var failed = await SyncAsync(Select(albumA: false, playlist: true));

        Assert.False(failed.Completed);
        Assert.Contains(failed.Failures, f => f.Contains("not updated", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllText(Local("/Playlists/Road Trip.m3u8")));
        AssertEveryPlaylistResolves(await ManifestAsync());

        _client.PartFiles[2] = a2File;
        var retry = await SyncAsync(Select(albumA: false, playlist: true));
        Assert.True(retry.Completed, string.Join("\n", retry.Failures));
        Assert.Contains("02 Hoppípolla.flac", File.ReadAllText(Local("/Playlists/Road Trip.m3u8")), StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task Stop_LeavesAConsistentDevice_AndResyncCompletes()
    {
        await LibraryAsync();
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var stopped = await SyncAsync(Select(playlist: true), stop: stop.Token);

        Assert.True(stopped.Stopped);
        Assert.False(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        Assert.Empty(Directory.GetFiles(_ipod, "*" + DevicePaths.TempSuffix, SearchOption.AllDirectories));

        var resumed = await SyncAsync(Select(playlist: true));
        Assert.True(resumed.Completed, string.Join("\n", resumed.Failures));
        AssertEveryPlaylistResolves(await ManifestAsync());
    }

    [FfmpegFact]
    public async Task InterruptedSync_LeftoverTempFile_IsCleanedUp()
    {
        await LibraryAsync();
        await SyncAsync(Select());
        Plant("/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac" + DevicePaths.TempSuffix, "half a file from a pulled cable");

        var result = await SyncAsync(Select());

        Assert.Contains(result.Warnings, w => w.Contains("unfinished temporary", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_ipod, "*" + DevicePaths.TempSuffix, SearchOption.AllDirectories));
    }

    [FfmpegFact]
    public async Task LostManifest_IsRebuiltFromTheFilesOwnTags_WithoutRecopying()
    {
        await LibraryAsync();
        await SyncAsync(Select(playlist: true));
        File.Delete(Path.Combine(_ipod, ".plexpod", "manifest.json"));
        File.Delete(Path.Combine(_ipod, ".plexpod", "manifest.prev.json"));
        var downloads = _client.Downloads;

        var result = await SyncAsync(Select(playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(0, result.Added);
        Assert.Equal(downloads, _client.Downloads);
        Assert.Equal(3, (await ManifestAsync()).Tracks.Count);
        Assert.False(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli (2).flac")));
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task TestExportCopiedOntoTheIpod_IsAdopted_NotDuplicated()
    {
        await LibraryAsync();
        var paths = new MediaPaths(_dir.File("cache"));
        var runner = TestFfmpeg.Runner;
        var exporter = new TestExportService(
            new TrackPipeline(paths, new ConversionCache(paths), new FlacConverter(runner), new ContainerCleaner(runner), new AudioVerifier(runner), NullLogger<TrackPipeline>.Instance),
            new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance),
            NullLogger<TestExportService>.Instance);
        var export = _dir.File("export");
        var resolved = SelectionResolver.Resolve(_snapshot, Select(playlist: true), _contents);
        await exporter.ExportAsync(_client, await TestFfmpeg.InstallAsync(),
            new ExportRequest(export, _snapshot, [.. resolved.TrackKeys.Select(k => _snapshot.FindTrack(k)!)], [(_snapshot.FindPlaylist("p1")!, _contents["p1"])], TagPolicy.Default),
            null, CancellationToken.None);
        CopyTree(Path.Combine(export, "Music"), Path.Combine(_ipod, "Music"));
        CopyTree(Path.Combine(export, "Playlists"), Path.Combine(_ipod, "Playlists"));
        var downloads = _client.Downloads;

        var result = await SyncAsync(Select(playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(0, result.Added);
        Assert.Equal(downloads, _client.Downloads);
        Assert.Contains(result.Warnings, w => w.StartsWith("Adopted 3 track(s) and 1 playlist(s)", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_ipod, "* (2).*", SearchOption.AllDirectories));
        Assert.StartsWith("#EXTM3U\n#PLEXPOD ratingKey=p1\n", File.ReadAllText(Local("/Playlists/Road Trip.m3u8")), StringComparison.Ordinal);
        AssertEveryPlaylistResolves(await ManifestAsync());
        AssertUserFilesUntouched();
    }


    // ---------------------------------------------------------------- review fixes (hardening)

    [FfmpegFact]
    public async Task TamperedManifest_PathsOutsideMusicAndPlaylists_AreNeverTouched()
    {
        await LibraryAsync();
        await SyncAsync(Select());
        Directory.CreateDirectory(Path.Combine(_ipod, ".rockbox"));
        File.WriteAllText(Path.Combine(_ipod, ".rockbox", "rockbox.ipod"), "firmware");

        // A manifest edited (or crafted) to claim Rockbox's firmware and a path off the device.
        var path = Path.Combine(_ipod, ".plexpod", "manifest.json");
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var tracks = json["tracks"]!.AsArray();
        tracks.Add(new JsonObject { ["devicePath"] = "/.rockbox/rockbox.ipod", ["desiredPath"] = "/.rockbox/rockbox.ipod", ["ratingKey"] = "zz1" });
        tracks.Add(new JsonObject { ["devicePath"] = "/C:/Windows/win.ini", ["desiredPath"] = "/C:/Windows/win.ini", ["ratingKey"] = "zz2" });
        json["playlists"]!.AsArray().Add(new JsonObject { ["ratingKey"] = "zz3", ["devicePath"] = "/../evil.m3u8", ["title"] = "x", ["contentHash"] = "h" });
        File.WriteAllText(path, json.ToJsonString());

        var result = await SyncAsync(Select());

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal("firmware", File.ReadAllText(Path.Combine(_ipod, ".rockbox", "rockbox.ipod")));
        Assert.Contains(result.Warnings, w => w.Contains("pointed outside /Music and /Playlists", StringComparison.Ordinal));
        Assert.DoesNotContain((await ManifestAsync()).Tracks, t => t.RatingKey.StartsWith("zz", StringComparison.Ordinal));
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task FileDeletedByHand_IsCopiedAgain_InsteadOfWedgingEverySync()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, playlist: true));
        File.Delete(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac"));

        var result = await SyncAsync(Select(albumA: true, playlist: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(1, result.Added);
        Assert.True(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac")));
        Assert.Contains(result.Warnings, w => w.Contains("missing from the iPod", StringComparison.Ordinal));
        AssertEveryPlaylistResolves(await ManifestAsync());
    }

    [FfmpegFact]
    public async Task PlexErrorAfterAMove_KeepsTheMoveRecorded_AndTheNextSyncFinishes()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());
        SetLibrary([a1 with { Title = "Glósóli (Live)", UpdatedAt = 5 }, a2, b1]);
        _client.BeforeAlbumDetail = (_, _) => throw new HttpRequestException("Plex went away");

        var failed = await SyncAsync(Select());

        Assert.False(failed.Completed);
        Assert.Contains(failed.Failures, f => f.Contains("couldn't read its details", StringComparison.Ordinal));
        Assert.True(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli (Live).flac")));
        Assert.Contains((await ManifestAsync()).Tracks, t => t.DevicePath == "/Music/Sigur Rós/2005 - Takk/01 Glósóli (Live).flac");

        _client.BeforeAlbumDetail = null;
        var retry = await SyncAsync(Select());
        Assert.True(retry.Completed, string.Join("\n", retry.Failures));
        Assert.Contains(TagWriter.ReadBack(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli (Live).flac")).Fields, f => f is { Key: "TITLE", Value: "Glósóli (Live)" });
    }

    [FfmpegFact]
    public async Task StopDuringTheTagCheck_IsACleanStop_NotAnError()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());
        SetLibrary([a1 with { UpdatedAt = 7 }, a2, b1]);
        using var stop = new CancellationTokenSource();
        _client.BeforeAlbumDetail = async (_, ct) =>
        {
            await stop.CancelAsync();
            ct.ThrowIfCancellationRequested();
        };

        var engine = Engine();
        var preview = await engine.PreviewAsync(await RequestAsync(Select()), CancellationToken.None);
        var stopped = await engine.ExecuteAsync(preview, null, stop.Token);

        Assert.True(stopped.Stopped);
        Assert.False(stopped.Completed);
        Assert.True(File.Exists(Path.Combine(_ipod, ".plexpod", SyncEngine.ReportFileName)));

        _client.BeforeAlbumDetail = null;
        Assert.True((await SyncAsync(Select())).Completed);
    }

    [FfmpegFact]
    public async Task UnexpectedErrorOnOneTrack_FailsOnlyThatTrack()
    {
        await LibraryAsync();
        _client.ExplodeOnDownload.Add(2);

        var result = await SyncAsync(Select());

        Assert.False(result.Completed);
        Assert.Equal(1, result.Added);
        Assert.Contains(result.Failures, f => f.Contains("simulated unexpected failure", StringComparison.Ordinal));
        Assert.True(File.Exists(Local("/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac")));
        Assert.Single((await ManifestAsync()).Tracks);
    }

    [FfmpegFact]
    public async Task MalformedAudioFileOfTheUsers_DoesNotBreakPlanning()
    {
        await LibraryAsync();
        Directory.CreateDirectory(Local("/Music/User Stuff"));
        File.WriteAllBytes(Local("/Music/User Stuff/broken.flac"), [.. "fLaC"u8, 0x80, 0, 0, 34, .. new byte[10]]);
        File.WriteAllBytes(Local("/Music/User Stuff/garbage.m4a"), [0, 0, 0, 8, .. "ftyp"u8, 0xFF, 0xFF, 0xFF, 0xFF]);

        var result = await SyncAsync(Select());

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.True(File.Exists(Local("/Music/User Stuff/broken.flac")));
        Assert.True(File.Exists(Local("/Music/User Stuff/garbage.m4a")));
    }

    [FfmpegFact]
    public async Task UnchangedUserFiles_AreNotReReadOnTheNextPlan()
    {
        await LibraryAsync();
        var engine = Engine();
        var first = await engine.PreviewAsync(await RequestAsync(Select()), CancellationToken.None);
        Assert.True(first.Adoption.Checked >= 1, "the user's mp3 is checked once");
        await engine.ExecuteAsync(first, null, CancellationToken.None);

        var second = await engine.PreviewAsync(await RequestAsync(Select()), CancellationToken.None);
        Assert.Equal(0, second.Adoption.Checked);

        File.WriteAllText(Local("/Music/User Stuff/my own.mp3"), "the user's file, edited");
        var third = await engine.PreviewAsync(await RequestAsync(Select()), CancellationToken.None);
        Assert.Equal(1, third.Adoption.Checked);
    }

    [FfmpegFact]
    public async Task DifferentDriveBetweenPlanAndSync_IsRefused_BeforeAnythingIsWritten()
    {
        await LibraryAsync();
        var fingerprint = "ipod-A";
        var engine = Engine(fingerprint: _ => fingerprint);
        var preview = await engine.PreviewAsync(await RequestAsync(Select()), CancellationToken.None);

        fingerprint = "ipod-B";
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(preview, null, CancellationToken.None));

        Assert.False(Directory.Exists(Local("/Music/Sigur Rós")));
        Assert.Equal(0, _client.Downloads);
    }

    [FfmpegFact]
    public async Task TitleStartingWithAnEllipsis_Syncs()
    {
        var (a1, a2, b1) = await LibraryAsync();
        SetLibrary([a1, a2, b1 with { Title = "...It's Just Wide-Open Field", Index = 8 }]);

        var result = await SyncAsync(Select(albumA: false, albumB: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.True(File.Exists(Local("/Music/Joni/1971 - Blue/08 ...It's Just Wide-Open Field.mp3")));
    }

    [FfmpegFact]
    public async Task StalePlaylist_WithABrokenLine_IsRemoved_NotBlockingTheSync()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, playlist: true));
        File.Delete(Local("/Music/Joni/1971 - Blue/01 River.mp3")); // deleted by hand; only the playlist used it

        var result = await SyncAsync(Select(albumA: true)); // the playlist is no longer selected

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(1, result.PlaylistsRemoved);
        Assert.False(File.Exists(Local("/Playlists/Road Trip.m3u8")));
        Assert.True((await SyncAsync(Select(albumA: true))).Completed);
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task CoverTheUserReplaced_IsKept_AndHandedOver()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());
        var cover = Local("/Music/Sigur Rós/2005 - Takk/cover.jpg");
        var mine = TagWriterTests.Jpeg(300, 300);
        File.WriteAllBytes(cover, mine);

        // New art in Plex (a new thumb, so not the cached image), and the album is touched.
        _client.Image = TagWriterTests.Jpeg(400, 400);
        _client.Albums["alA"] = _client.Albums["alA"] with { ThumbPath = "/thumb/a-new" };
        SetLibrary([a1 with { Title = "Glósóli (Remastered)", UpdatedAt = 2 }, a2, b1]);
        var result = await SyncAsync(Select());

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(mine, File.ReadAllBytes(cover));
        Assert.True(result.Warnings.Any(w => w.Contains("Kept your own", StringComparison.Ordinal)), string.Join("\n", result.Warnings) + $"\nretagged={result.Retagged} moved={result.Moved}");
        Assert.DoesNotContain((await ManifestAsync()).Extras, e => e.DevicePath.EndsWith("2005 - Takk/cover.jpg", StringComparison.Ordinal));

        await SyncAsync(Select(albumA: false, albumB: true)); // album A deselected: its folder goes, the user's cover stays
        Assert.Equal(mine, File.ReadAllBytes(cover));
    }

    // ---------------------------------------------------------------- rebrand (RockPlex → PlexPod)

    /// <summary>Turns this iPod into one synced before the rename: old folder, old playlist header.</summary>
    private void MakeLegacy()
    {
        var store = Path.Combine(_ipod, DeviceStore.LegacyFolderName);
        Directory.Move(Path.Combine(_ipod, DeviceStore.FolderName), store);
        var manifestPath = Path.Combine(store, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        foreach (var entry in manifest["playlists"]!.AsArray())
        {
            var local = Local(entry!["devicePath"]!.GetValue<string>());
            var legacy = File.ReadAllText(local).Replace(PlaylistWriter.HeaderPrefix, PlaylistWriter.LegacyHeaderPrefix, StringComparison.Ordinal);
            File.WriteAllText(local, legacy);
            entry["contentHash"] = PlaylistWriter.Hash(legacy);
        }

        File.WriteAllText(manifestPath, manifest.ToJsonString());
        File.Delete(Path.Combine(store, "manifest.prev.json"));
    }

    [FfmpegFact]
    public async Task IpodSyncedBeforeTheRename_IsMigrated_WithoutCopyingAnythingAgain()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, playlist: true));
        MakeLegacy();
        var downloads = _client.Downloads;

        var engine = Engine();
        var preview = await engine.PreviewAsync(await RequestAsync(Select(albumA: true, playlist: true)), CancellationToken.None);
        Assert.True(Directory.Exists(Path.Combine(_ipod, DeviceStore.LegacyFolderName))); // planning changes nothing
        Assert.False(Directory.Exists(Path.Combine(_ipod, DeviceStore.FolderName)));
        Assert.All(preview.Plan.TrackOps, o => Assert.Equal(TrackOpKind.Keep, o.Kind));
        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(downloads, _client.Downloads);
        Assert.Equal(0, result.BytesWritten); // track bytes: nothing copied again
        Assert.Contains(result.Warnings, w => w.StartsWith("Renamed /.rockplex to /.plexpod", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(_ipod, DeviceStore.LegacyFolderName)));
        Assert.True(File.Exists(Path.Combine(_ipod, DeviceStore.FolderName, "device.json")));
        Assert.StartsWith("#EXTM3U\n#PLEXPOD ratingKey=p1\n", File.ReadAllText(Local("/Playlists/Road Trip.m3u8")), StringComparison.Ordinal);
        Assert.True((await SyncAsync(Select(albumA: true, playlist: true))).Completed);
        AssertEveryPlaylistResolves(await ManifestAsync());
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task TrackTaggedBeforeTheRename_StampOnlyChange_IsNotCopiedAgain()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select());

        // The manifest's tag hash as the old app computed it: same tags, ROCKPLEX_* ID names.
        var path = "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac";
        var legacyHash = TagSet.HashFields(TagWriter.ReadBack(Local(path)).Fields.Select(f => f.Key switch
        {
            TagSet.RatingKeyKey => new KeyValuePair<string, string>(TagSet.LegacyRatingKeyKey, f.Value),
            TagSet.GuidKey => new KeyValuePair<string, string>(TagSet.LegacyGuidKey, f.Value),
            TagSet.PartIdKey => new KeyValuePair<string, string>(TagSet.LegacyPartIdKey, f.Value),
            _ => f,
        }));
        var manifestPath = Path.Combine(_ipod, DeviceStore.FolderName, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["tracks"]!.AsArray().Single(t => t!["devicePath"]!.GetValue<string>() == path)!["tagHash"] = legacyHash;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var downloads = _client.Downloads;

        SetLibrary([a1 with { UpdatedAt = 99 }, a2, b1]); // e.g. a Plex library refresh
        var result = await SyncAsync(Select());

        Assert.Equal(1, result.TagsAlreadyCurrent);
        Assert.Equal(downloads, _client.Downloads);
        Assert.Equal(0, result.BytesWritten);
    }

    [FfmpegFact]
    public async Task LegacyIdTags_AreAdopted_WhenTheManifestIsLost()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true));
        foreach (var path in new[] { "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac", "/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac" })
        {
            using var file = TagLib.File.Create(Local(path));
            var xiph = (TagLib.Ogg.XiphComment)file.GetTag(TagLib.TagTypes.Xiph);
            foreach (var (key, legacy) in new[] { (TagSet.RatingKeyKey, TagSet.LegacyRatingKeyKey), (TagSet.GuidKey, TagSet.LegacyGuidKey), (TagSet.PartIdKey, TagSet.LegacyPartIdKey) })
            {
                xiph.SetField(legacy, xiph.GetFirstField(key));
                xiph.RemoveField(key);
            }

            file.Save();
        }

        MakeLegacy();
        File.Delete(Path.Combine(_ipod, DeviceStore.LegacyFolderName, "manifest.json"));
        var downloads = _client.Downloads;

        var result = await SyncAsync(Select(albumA: true));

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(0, result.Added);
        Assert.Equal(downloads, _client.Downloads);
        Assert.Contains(result.Warnings, w => w.StartsWith("Adopted 2 track(s)", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(_ipod, "* (2).*", SearchOption.AllDirectories));
    }

    // ---------------------------------------------------------------- M4: stats return

    private const string PathA1 = "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac";
    private const string PathA2 = "/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac";
    private const string PathB1 = "/Music/Joni/1971 - Blue/01 River.mp3";

    private static readonly StatsOptions StatsOn = new(RatingMode.IpodWins);

    private string RockboxFile(string name) => Path.Combine(_ipod, ".rockbox", name);

    private void WriteLog(string name, params string[] lines) =>
        File.WriteAllText(RockboxFile(name), "# Started Ver. 4.0 " + name + "\n" + string.Concat(lines.Select(l => l + "\n")));

    private static string Play(string path, long elapsed = 200_000, long length = 200_000, long at = 1_760_000_000) => $"{at}:{elapsed}:{length}:{path}";

    private StatsService Stats() => new(new MediaPaths(_dir.File("cache")), NullLogger<StatsService>.Instance) { Zone = TimeZoneInfo.Utc };

    [FfmpegFact]
    public async Task Stats_PlaysSentBeforeRemovals_IncludingTheRemovedTrack_AndRockboxFilesUntouched()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: true));
        WriteLog("playback.log", Play(PathA1), Play(PathB1), Play(PathA2, elapsed: 10_000), Play("/Music/User Stuff/my own.mp3"));
        var logBefore = File.ReadAllBytes(RockboxFile("playback.log"));
        var logTime = File.GetLastWriteTimeUtc(RockboxFile("playback.log"));

        var engine = Engine();
        var preview = await engine.PreviewAsync(await RequestAsync(Select(albumA: true, albumB: false), stats: StatsOn), CancellationToken.None);
        Assert.Equal(2, preview.Stats!.Plays);
        Assert.Equal("2 plays", preview.Stats.Summary);
        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);

        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(["play:t1", "play:t3"], _client.Writes);
        Assert.False(File.Exists(Local(PathB1))); // removed in the same sync, after its play was sent
        Assert.Equal(2, result.Stats!.PlaysSent);
        Assert.Equal(1, result.Stats.TooShort);
        Assert.Equal(1, result.Stats.NotOurs);
        Assert.Contains("Sent 2 plays", result.Report, StringComparison.Ordinal);
        Assert.Equal(logBefore, File.ReadAllBytes(RockboxFile("playback.log")));
        Assert.Equal(logTime, File.GetLastWriteTimeUtc(RockboxFile("playback.log")));

        var deviceId = (await new DeviceStore(_ipod).TryReadIdentityAsync(CancellationToken.None))!.DeviceId;
        var history = File.ReadAllLines(Path.Combine(_dir.Path, "stats", deviceId, "plays.jsonl"));
        Assert.Equal(2, history.Length);
        Assert.Contains("\"playedAt\":\"2025-10-09T08:53:20+00:00\"", history[0], StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "stats", deviceId, "logs")));

        // The same log again: nothing more is sent.
        await SyncAsync(Select(albumA: true), stats: StatsOn);
        Assert.Equal(2, _client.Writes.Count);
        AssertUserFilesUntouched();
    }

    [FfmpegFact]
    public async Task Stats_PlexDown_SyncStillRuns_AndPlaysAreSentNextTime()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: true));
        WriteLog("playback.log", Play(PathA1), Play(PathB1));

        _client.FailWrite = _ => _client.Writes.Count >= 1; // Plex goes away after the first play
        var result = await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);
        Assert.True(result.Completed, string.Join("\n", result.Failures));
        Assert.Equal(["play:t1"], _client.Writes);
        Assert.Equal(1, result.Stats!.PlaysWaiting);
        Assert.Contains("Couldn't reach Plex. 1 play will be sent next time.", result.Report, StringComparison.Ordinal);

        _client.FailWrite = null;
        File.AppendAllText(RockboxFile("playback.log"), Play(PathA2) + "\n");
        await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);
        Assert.Equal(["play:t1", "play:t3", "play:t2"], _client.Writes);
    }

    [FfmpegFact]
    public async Task Stats_RotatedLog_IsReadOnceInOrder()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: true));
        WriteLog("playback.log", Play(PathA1));
        await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);

        // Rockbox renamed the log at start-up (after one more play) and began a new one.
        File.AppendAllText(RockboxFile("playback.log"), Play(PathB1) + "\n");
        File.Move(RockboxFile("playback.log"), RockboxFile("playback_0001.log"));
        WriteLog("playback.log", Play(PathA2));
        await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);

        Assert.Equal(["play:t1", "play:t3", "play:t2"], _client.Writes);
    }

    [FfmpegFact]
    public async Task Stats_Ratings_FromTheChangelog_SentOnceAndRespectTheSetting()
    {
        var (a1, a2, b1) = await LibraryAsync();
        await SyncAsync(Select(albumA: true, albumB: true));
        File.WriteAllText(RockboxFile("database_changelog.txt"),
            $"## Changelog version 1\nfilename=\"{PathA1}\" rating=\"8\"\nfilename=\"{PathA2}\" rating=\"0\"\nfilename=\"{PathB1}\" rating=\"6\"\n");

        SetLibrary([a1, a2, b1 with { UserRating = 4 }]);
        await SyncAsync(Select(albumA: true, albumB: true), stats: new StatsOptions(RatingMode.FillGapsOnly));
        Assert.Equal(["rate:t1=8"], _client.Writes); // B1 is rated in Plex; A2 was never rated

        await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);
        Assert.Equal(["rate:t1=8", "rate:t3=6"], _client.Writes); // iPod wins now; t1 unchanged

        await SyncAsync(Select(albumA: true, albumB: true), stats: StatsOn);
        Assert.Equal(2, _client.Writes.Count);
        Assert.Contains("rating=\"8\"", File.ReadAllText(RockboxFile("database_changelog.txt")), StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task Stats_Off_SendsNothing_AndWritesNoState()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true));
        WriteLog("playback.log", Play(PathA1));

        var result = await SyncAsync(Select(albumA: true));

        Assert.Null(result.Stats);
        Assert.Empty(_client.Writes);
        Assert.False(File.Exists(Path.Combine(_ipod, ".plexpod", "stats.json")));
    }

    [FfmpegFact]
    public async Task Stats_NoLog_HintsAtLogging()
    {
        await LibraryAsync();
        await SyncAsync(Select(albumA: true));
        var result = await SyncAsync(Select(albumA: true), stats: StatsOn);
        Assert.Contains("Playback Settings → Logging", result.Report, StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task SendPlaysNow_SendsWithoutSyncing_AndTheNextSyncDoesntResend()
    {
        await LibraryAsync();
        var none = await Stats().SendNowAsync(_ipod, _client, StatsOn, _ => false, CancellationToken.None);
        Assert.Contains("hasn't synced this iPod", Assert.Single(none.Lines), StringComparison.Ordinal);

        await SyncAsync(Select(albumA: true));
        WriteLog("playback.log", Play(PathA1), Play(PathA2));
        var engine = Engine();
        var preview = await engine.PreviewAsync(await RequestAsync(Select(albumA: true), stats: StatsOn), CancellationToken.None);
        Assert.Equal(2, preview.Stats!.Plays);

        var now = await Stats().SendNowAsync(_ipod, _client, StatsOn, _ => false, CancellationToken.None);
        Assert.Equal(2, now.PlaysSent);

        // The plan made before Send plays now still says 2, but the sync re-reads and sends none.
        var result = await engine.ExecuteAsync(preview, null, CancellationToken.None);
        Assert.Equal(0, result.Stats!.PlaysSent);
        Assert.Equal(["play:t1", "play:t2"], _client.Writes);
        Assert.Equal("No new plays or ratings on the iPod.", Assert.Single((await Stats().SendNowAsync(_ipod, _client, StatsOn, _ => false, CancellationToken.None)).Lines));
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
