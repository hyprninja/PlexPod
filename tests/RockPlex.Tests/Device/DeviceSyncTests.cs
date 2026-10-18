using System.Security.Cryptography;
using RockPlex.Core.Sync;
using RockPlex.Device.Sync;

namespace RockPlex.Tests.Device;

/// <summary>A temp folder stands in for the iPod's drive root.</summary>
public sealed class DeviceSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rockplex-tests", Guid.NewGuid().ToString("N"));
    private readonly DeviceFileOps _ops = new();

    public DeviceSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Local(string devicePath) => DevicePaths.ToLocal(_root, devicePath);

    private string PcFile(byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), "rockplex-tests", Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private void Plant(string devicePath, string text = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Local(devicePath))!);
        File.WriteAllText(Local(devicePath), text);
    }

    [Fact]
    public async Task Manifest_RoundTrips_AndKeepsThePreviousVersion()
    {
        var store = new DeviceStore(_root);
        var first = DeviceManifest.Empty("dev-1") with
        {
            Tracks = [new ManifestTrack { DevicePath = "/Music/A/01 Ó.flac", DesiredPath = "/Music/A/01 Ó.flac", RatingKey = "1", SizeBytes = 5 }],
        };
        await store.SaveManifestAsync(first, CancellationToken.None);
        await store.SaveManifestAsync(first with { Tracks = [] }, CancellationToken.None);

        var (loaded, source) = await store.LoadManifestAsync(CancellationToken.None);
        Assert.Equal(ManifestSource.Current, source);
        Assert.Empty(loaded!.Tracks);
        Assert.True(File.Exists(Path.Combine(_root, ".rockplex", "manifest.prev.json")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".rockplex"), "*" + DevicePaths.TempSuffix));
    }

    [Fact]
    public async Task CorruptManifest_FallsBackToPrevious()
    {
        var store = new DeviceStore(_root);
        var good = DeviceManifest.Empty("dev-1") with { Tracks = [new ManifestTrack { DevicePath = "/Music/a.flac", DesiredPath = "/Music/a.flac", RatingKey = "1" }] };
        await store.SaveManifestAsync(good, CancellationToken.None);
        await store.SaveManifestAsync(good, CancellationToken.None);
        File.WriteAllText(Path.Combine(_root, ".rockplex", "manifest.json"), "{ truncated by an unplug");

        var (loaded, source) = await store.LoadManifestAsync(CancellationToken.None);
        Assert.Equal(ManifestSource.Previous, source);
        Assert.Single(loaded!.Tracks);
    }

    [Fact]
    public async Task NoManifest_IsNone()
    {
        var (loaded, source) = await new DeviceStore(_root).LoadManifestAsync(CancellationToken.None);
        Assert.Null(loaded);
        Assert.Equal(ManifestSource.None, source);
    }

    [Fact]
    public async Task Identity_IsCreatedOnce()
    {
        var store = new DeviceStore(_root);
        var a = await store.LoadOrCreateIdentityAsync("IPOD", "ipodvideo", CancellationToken.None);
        var b = await store.LoadOrCreateIdentityAsync("OTHER", "x", CancellationToken.None);
        Assert.Equal(a.DeviceId, b.DeviceId);
        Assert.Equal("IPOD", b.VolumeLabel);
    }

    [Fact]
    public async Task CopyVerified_WritesExactBytes_ReportsFolders_LeavesNoTemp()
    {
        var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17); // not sector-aligned on purpose
        var result = await _ops.CopyVerifiedAsync(PcFile(bytes), _root, "/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac", CancellationToken.None);

        Assert.Equal(bytes, File.ReadAllBytes(Local("/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac")));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
        Assert.Equal(bytes.Length, result.SizeBytes);
        Assert.Equal(["/Music", "/Music/Sigur Rós", "/Music/Sigur Rós/2005 - Takk"], result.CreatedFolders);
        Assert.Empty(Directory.GetFiles(_root, "*" + DevicePaths.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CopyVerified_OverwritesInPlace_ViaTempAndRename()
    {
        Plant("/Music/a.flac", "old");
        await _ops.CopyVerifiedAsync(PcFile("new content"u8.ToArray()), _root, "/Music/a.flac", CancellationToken.None);
        Assert.Equal("new content", File.ReadAllText(Local("/Music/a.flac")));
    }

    [Fact]
    public void HashFromDevice_MatchesBufferedHash_ForOddSizes()
    {
        foreach (var size in new[] { 0, 1, 4095, 4096, 4097, (1 << 20) + 3 })
        {
            var path = PcFile(RandomNumberGenerator.GetBytes(size));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), DeviceFileOps.HashFromDevice(path));
        }
    }

    [Fact]
    public async Task TextWrite_IsUtf8WithoutBom()
    {
        await _ops.WriteTextVerifiedAsync("#EXTM3U\n/Music/Mötley Crüe/a.flac\n", _root, "/Playlists/Road Trip.m3u8", CancellationToken.None);
        var bytes = File.ReadAllBytes(Local("/Playlists/Road Trip.m3u8"));
        Assert.Equal((byte)'#', bytes[0]);
        Assert.Contains("Mötley Crüe", File.ReadAllText(Local("/Playlists/Road Trip.m3u8")), StringComparison.Ordinal);
    }

    [Fact]
    public void Move_RenamesWithoutCopy_IncludingCaseOnlyChanges()
    {
        Plant("/Music/Old/a.flac", "audio");
        var created = _ops.Move(_root, "/Music/Old/a.flac", "/Music/New/a.flac");
        Assert.Equal(["/Music/New"], created);
        Assert.Equal("audio", File.ReadAllText(Local("/Music/New/a.flac")));

        _ops.Move(_root, "/Music/New/a.flac", "/Music/New/A.flac");
        Assert.Contains("/Music/New/A.flac", DeviceFileOps.ListFiles(_root, "Music"));
    }

    [Fact]
    public void Move_NeverOverwritesAnExistingFile()
    {
        Plant("/Music/a.flac", "rockplex");
        Plant("/Music/b.flac", "user");
        Assert.ThrowsAny<IOException>(() => _ops.Move(_root, "/Music/a.flac", "/Music/b.flac"));
        Assert.Equal("user", File.ReadAllText(Local("/Music/b.flac")));
    }

    [Fact]
    public void EmptyFolders_OnlyEmptyOnesAreRemoved_DeepestFirst()
    {
        Directory.CreateDirectory(Local("/Music/A/2000 - B"));
        Plant("/Music/C/keep.flac");
        var removed = DeviceFileOps.DeleteEmptyFolders(_root, ["/Music/A", "/Music/A/2000 - B", "/Music/C"]);
        Assert.Equal(["/Music/A/2000 - B", "/Music/A"], removed);
        Assert.True(Directory.Exists(Local("/Music/C")));
    }

    [Fact]
    public void TempFiles_AreCleaned_AndHiddenFromListings()
    {
        Plant("/Music/A/x.flac" + DevicePaths.TempSuffix);
        Plant("/Music/A/real.flac");
        Assert.Equal(["/Music/A/real.flac"], DeviceFileOps.ListFiles(_root, "Music"));
        Assert.Equal(1, DeviceFileOps.CleanTempFiles(_root));
        Assert.False(File.Exists(Local("/Music/A/x.flac" + DevicePaths.TempSuffix)));
    }

    [Fact]
    public void PlaylistCheck_ResolvesExactCase_AndReportsEveryMiss()
    {
        Plant("/Music/Artist/01 Song.flac");
        Plant("/Playlists/Mix.m3u8", "#EXTM3U\n#EXTINF:1,a\n/Music/Artist/01 Song.flac\n/Music/artist/01 Song.flac\n/Music/Artist/02 Gone.flac\nrelative.flac\n");

        var problems = new PlaylistVerifier().Verify(_root, ["/Playlists/Mix.m3u8", "/Playlists/Missing.m3u8"]);

        Assert.Equal(4, problems.Count);
        Assert.Contains(problems, p => p.LineNumber == 4 && p.Reason.StartsWith("case differs", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.LineNumber == 5 && p.Reason == "file missing");
        Assert.Contains(problems, p => p.LineNumber == 6 && p.Reason.Contains("absolute", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.PlaylistPath == "/Playlists/Missing.m3u8");
    }

    [Fact]
    public void DevicePaths_RejectTraversal()
    {
        Assert.Throws<ArgumentException>(() => DevicePaths.ToLocal(_root, "/Music/../Windows/x"));
        Assert.Throws<ArgumentException>(() => DevicePaths.ToLocal(_root, "Music/x"));
        Assert.Equal(["/Music", "/Music/A"], DevicePaths.ParentFolders("/Music/A/b.flac"));
    }
}
