using PlexPod.Core.Stats;
using PlexPod.Core.Sync;
using PlexPod.Device.Sync;

namespace PlexPod.Tests.Device;

/// <summary>An iPod synced before the rename (as RockPlex) keeps working, and moves to the new names.</summary>
public sealed class RebrandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public RebrandTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Legacy => Path.Combine(_root, DeviceStore.LegacyFolderName);

    private string Current => Path.Combine(_root, DeviceStore.FolderName);

    private void PlantLegacyStore()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "device.json"), """{"deviceId":"11111111-2222-3333-4444-555555555555","firstSync":"2026-10-01T00:00:00+00:00"}""");
        File.WriteAllText(Path.Combine(Legacy, "manifest.json"), """{"deviceId":"11111111-2222-3333-4444-555555555555","tracks":[],"playlists":[],"extras":[],"folders":[]}""");
    }

    [Fact]
    public async Task LegacyFolder_IsReadWithoutChangingTheIpod()
    {
        PlantLegacyStore();
        var store = new DeviceStore(_root);

        Assert.Equal("11111111-2222-3333-4444-555555555555", (await store.TryReadIdentityAsync(CancellationToken.None))!.DeviceId);
        Assert.Equal(ManifestSource.Current, (await store.LoadManifestAsync(CancellationToken.None)).Source);
        Assert.Same(StatsState.Empty, await store.LoadStatsAsync(CancellationToken.None));

        Assert.True(Directory.Exists(Legacy));
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public async Task AnyWrite_MovesTheLegacyFolderFirst()
    {
        PlantLegacyStore();
        var store = new DeviceStore(_root);

        await store.SaveStatsAsync(StatsState.Empty, CancellationToken.None);

        Assert.False(Directory.Exists(Legacy));
        Assert.True(File.Exists(Path.Combine(Current, "stats.json")));
        Assert.True(File.Exists(Path.Combine(Current, "device.json")));
        Assert.True(File.Exists(Path.Combine(Current, "manifest.json")));
        Assert.False(store.MigrateLegacyFolder());
    }

    [Fact]
    public async Task BothFolders_TheNewOneWins_AndTheOldOneIsLeftAlone()
    {
        PlantLegacyStore();
        Directory.CreateDirectory(Current);
        var store = new DeviceStore(_root);

        Assert.Null(await store.TryReadIdentityAsync(CancellationToken.None));
        Assert.False(store.MigrateLegacyFolder());
        Assert.True(File.Exists(Path.Combine(Legacy, "device.json")));
    }

    [Fact]
    public void LegacyTempFiles_AreCleanedUp()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Music", "A"));
        File.WriteAllText(Path.Combine(_root, "Music", "A", "01 x.flac" + DevicePaths.LegacyTempSuffix), "partial");
        File.WriteAllText(Path.Combine(_root, "Music", "A", "02 y.flac" + DevicePaths.TempSuffix), "partial");
        File.WriteAllText(Path.Combine(_root, "Music", "A", "03 z.flac"), "kept");

        Assert.Equal(2, DeviceFileOps.CleanTempFiles(_root));
        Assert.Equal(["/Music/A/03 z.flac"], DeviceFileOps.ListFiles(_root, "Music"));
    }

    [Fact]
    public void LegacyPlaylistHeader_IsRecognised()
    {
        Assert.Equal("48211", PlaylistWriter.ReadRatingKey(["#EXTM3U", "#ROCKPLEX ratingKey=48211"]));
        Assert.Equal("48211", PlaylistWriter.ReadRatingKey(["#EXTM3U", "#PLEXPOD ratingKey=48211"]));
    }
}
