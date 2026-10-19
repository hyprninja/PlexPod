using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.App.Services;
using PlexPod.Core.Tagging;

namespace PlexPod.Tests.App;

/// <summary>Two stores on one folder stand in for two PlexPod windows (say, the installed app and a dev build).</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-settings-" + Guid.NewGuid().ToString("N"), AppPaths.FolderName);

    public void Dispose()
    {
        var parent = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private SettingsStore Open(TimeSpan? lockTimeout = null) =>
        new(new AppPaths(_root), NullLogger<SettingsStore>.Instance) { LockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task AnotherWindowsSave_ToADifferentSetting_DoesNotUndoThisOne()
    {
        var installed = Open();
        var dev = Open();
        var clientId = installed.Current.ClientIdentifier;
        _ = dev.Current; // both have loaded the same settings

        var mine = TagPolicy.Default with { Date = false, Composer = false, MusicBrainzTrackId = true };
        await installed.UpdateAsync(s => s with { TagPolicy = mine }, CancellationToken.None);
        await dev.UpdateAsync(s => s with { FfmpegPath = @"C:\tools\ffmpeg.exe" }, CancellationToken.None); // as every start does

        var onDisk = Open().Current;
        Assert.Equal(mine, onDisk.TagPolicy);
        Assert.Equal(@"C:\tools\ffmpeg.exe", onDisk.FfmpegPath);
        Assert.Equal(clientId, onDisk.ClientIdentifier);
    }

    [Fact]
    public async Task Current_PicksUpWhatAnotherWindowSaved()
    {
        var installed = Open();
        var dev = Open();
        Assert.Equal(TagPolicy.Default, dev.Current.TagPolicy);

        var changed = TagPolicy.Default with { Genre = false };
        await installed.UpdateAsync(s => s with { TagPolicy = changed }, CancellationToken.None);

        Assert.Equal(changed, dev.Current.TagPolicy);
    }

    [Fact]
    public async Task Save_WaitsForAnotherWindowsSaveToFinish()
    {
        var store = Open();
        _ = store.Current;
        Task save;
        using (new FileStream(Path.Combine(_root, "settings.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            save = store.UpdateAsync(s => s with { SectionKey = "7" }, CancellationToken.None);
            await Task.Delay(200);
            Assert.False(save.IsCompleted);
        }

        await save.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("7", Open().Current.SectionKey);
    }

    [Fact]
    public async Task Save_GoesAheadIfTheLockIsNeverReleased()
    {
        var store = Open(TimeSpan.FromMilliseconds(100));
        _ = store.Current;
        using var stuck = new FileStream(Path.Combine(_root, "settings.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        await store.UpdateAsync(s => s with { SectionKey = "7" }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("7", Open().Current.SectionKey);
    }

    [Fact]
    public async Task UnreadableFile_KeepsTheLoadedSettings_AndTheClientIdentifier()
    {
        var store = Open();
        var clientId = store.Current.ClientIdentifier;
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), "{ not json");

        Assert.Equal(clientId, store.Current.ClientIdentifier);
        await store.UpdateAsync(s => s with { SectionKey = "7" }, CancellationToken.None);
        Assert.Equal(clientId, Open().Current.ClientIdentifier);
    }
}
