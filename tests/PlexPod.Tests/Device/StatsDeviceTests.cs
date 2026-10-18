using PlexPod.Core.Stats;
using PlexPod.Device.Stats;
using PlexPod.Device.Sync;

namespace PlexPod.Tests.Device;

/// <summary>A temp folder stands in for the iPod's drive root.</summary>
public sealed class StatsDeviceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public StatsDeviceTests() => Directory.CreateDirectory(Path.Combine(_root, ".rockbox"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Rockbox(string name) => Path.Combine(_root, ".rockbox", name);

    [Fact]
    public async Task Read_NoLogs_ReportsLoggingOff()
    {
        var files = await StatsReader.ReadAsync(_root, CancellationToken.None);
        Assert.True(files.HasNoLog);
        Assert.Null(files.Changelog);
        Assert.False(files.ChangelogLooksStale);
    }

    [Fact]
    public async Task Read_OrdersRotatedLogsNumerically_ThenCurrent()
    {
        File.WriteAllText(Rockbox("playback.log"), "# c\n");
        File.WriteAllText(Rockbox("playback_0010.log"), "# 10\n");
        File.WriteAllText(Rockbox("playback_0002.log"), "# 2\n");
        File.WriteAllText(Rockbox("playback_x.log"), "# junk\n");
        File.WriteAllText(Rockbox("other.log"), "# other\n");
        var files = await StatsReader.ReadAsync(_root, CancellationToken.None);
        Assert.Equal(["playback_0002.log", "playback_0010.log", "playback.log"], files.Logs.Select(l => l.FileName));
    }

    [Fact]
    public async Task Read_OpenLogStillReadable_AndFilesUnchanged()
    {
        var path = Rockbox("playback.log");
        File.WriteAllText(path, "# c\n1:2:3:/Music/a.flac\n");
        var written = File.GetLastWriteTimeUtc(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var files = await StatsReader.ReadAsync(_root, CancellationToken.None);
            Assert.Single(files.Logs);
        }

        Assert.Equal("# c\n1:2:3:/Music/a.flac\n", File.ReadAllText(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Read_OversizedLog_IsSkipped()
    {
        using (var big = File.Create(Rockbox("playback.log")))
        {
            big.SetLength(StatsReader.MaxLogBytes + 1);
        }

        var files = await StatsReader.ReadAsync(_root, CancellationToken.None);
        Assert.Empty(files.Logs);
        Assert.Equal(["playback.log"], files.Skipped);
    }

    [Fact]
    public async Task Read_Changelog_StaleWhenOlderThanNewestLog()
    {
        File.WriteAllText(Rockbox("database_changelog.txt"), "## Changelog version 1\nfilename=\"/Music/a.flac\" rating=\"8\"\n");
        File.WriteAllText(Rockbox("playback.log"), "# c\n");
        File.SetLastWriteTimeUtc(Rockbox("database_changelog.txt"), DateTime.UtcNow.AddDays(-1));
        var files = await StatsReader.ReadAsync(_root, CancellationToken.None);
        Assert.Contains("rating=\"8\"", files.Changelog);
        Assert.True(files.ChangelogLooksStale);

        File.SetLastWriteTimeUtc(Rockbox("database_changelog.txt"), DateTime.UtcNow.AddDays(1));
        Assert.False((await StatsReader.ReadAsync(_root, CancellationToken.None)).ChangelogLooksStale);
    }

    [Fact]
    public async Task StatsState_RoundTrips()
    {
        var store = new DeviceStore(_root);
        Assert.Same(StatsState.Empty, await store.LoadStatsAsync(CancellationToken.None));
        var state = new StatsState
        {
            Bookmarks = [LogBookmark.At(new LogFile("playback.log", new byte[200]), 123)],
            RatingsSent = new Dictionary<string, int> { ["101"] = 8 },
        };
        await store.SaveStatsAsync(state, CancellationToken.None);
        var loaded = await store.LoadStatsAsync(CancellationToken.None);
        Assert.Equal(state.Bookmarks, loaded.Bookmarks);
        Assert.Equal(8, loaded.RatingsSent["101"]);
    }

    [Fact]
    public async Task StatsState_Hostile_IsSanitisedOrIgnored()
    {
        var store = new DeviceStore(_root);
        Directory.CreateDirectory(store.Folder);
        File.WriteAllText(Path.Combine(store.Folder, "stats.json"), """{"bookmarks":[null,{"prefixHash":"","processedBytes":5,"fileName":"x"},{"prefixHash":"0000000000000000000000000000000000000000000000000000000000000000","processedBytes":-4,"fileName":"y"},{"prefixHash":"0000000000000000000000000000000000000000000000000000000000000000","processedBytes":4,"fileName":"z"}],"ratingsSent":null}""");
        var loaded = await store.LoadStatsAsync(CancellationToken.None);
        Assert.Equal("z", Assert.Single(loaded.Bookmarks).FileName);
        Assert.Empty(loaded.RatingsSent);

        File.WriteAllText(Path.Combine(store.Folder, "stats.json"), "{ not json");
        Assert.Same(StatsState.Empty, await store.LoadStatsAsync(CancellationToken.None));
    }
}
