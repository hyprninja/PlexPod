using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RockPlex.Core.Models;
using RockPlex.Core.Selection;
using RockPlex.Core.Sync;
using RockPlex.Core.Tagging;
using RockPlex.Device.Sync;
using RockPlex.Media;
using RockPlex.Media.Art;
using RockPlex.Media.Ffmpeg;
using RockPlex.Media.Pipeline;
using RockPlex.Media.Sync;
using RockPlex.Media.Tags;

namespace RockPlex.Tests.Media;

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
        return new SyncEngine(pipeline, new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance), paths, new DeviceFileOps(), NullLogger<SyncEngine>.Instance)
        {
            FreeSpace = _ => free,
            Fingerprint = fingerprint ?? (_ => "test-ipod"),
        };
    }

    private async Task<SyncRequest> RequestAsync(SelectionProfile profile, TagPolicy? policy = null)
    {
        var resolved = SelectionResolver.Resolve(_snapshot, profile, _contents);
        return new SyncRequest(_ipod, "IPOD", "ipodvideo", _client, await TestFfmpeg.InstallAsync(), _snapshot, resolved, _contents,
            new Dictionary<string, AudioFormat>(), new Dictionary<string, long>(), policy ?? TagPolicy.Default, profile);
    }

    private static SelectionProfile Select(bool albumA = true, bool albumB = false, bool playlist = false) =>
        new("3", false,
            playlist ? [new ItemRef("p1", "g", "Road Trip")] : [],
            [],
            [.. albumA ? [new ItemRef("alA", "g", "Takk...")] : Array.Empty<ItemRef>(), .. albumB ? [new ItemRef("alB", "g", "Blue")] : Array.Empty<ItemRef>()]);

    private async Task<SyncResult> SyncAsync(SelectionProfile profile, long free = Plenty, TagPolicy? policy = null, CancellationToken stop = default)
    {
        var engine = Engine(free);
        var preview = await engine.PreviewAsync(await RequestAsync(profile, policy), CancellationToken.None);
        return await engine.ExecuteAsync(preview, null, stop);
    }

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
        Assert.True(File.Exists(Path.Combine(_ipod, ".rockplex", "device.json")));
        Assert.True(File.Exists(Path.Combine(_ipod, ".rockplex", SyncEngine.SelectionFileName)));
        Assert.Contains("Added 3", File.ReadAllText(Path.Combine(_ipod, ".rockplex", SyncEngine.ReportFileName)), StringComparison.Ordinal);
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
        Assert.False(File.Exists(Path.Combine(_ipod, ".rockplex", "manifest.json")));
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
        File.Delete(Path.Combine(_ipod, ".rockplex", "manifest.json"));
        File.Delete(Path.Combine(_ipod, ".rockplex", "manifest.prev.json"));
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
        Assert.StartsWith("#EXTM3U\n#ROCKPLEX ratingKey=p1\n", File.ReadAllText(Local("/Playlists/Road Trip.m3u8")), StringComparison.Ordinal);
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
        var path = Path.Combine(_ipod, ".rockplex", "manifest.json");
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
        Assert.True(File.Exists(Path.Combine(_ipod, ".rockplex", SyncEngine.ReportFileName)));

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
