using System.Diagnostics;
using System.Text;
using PlexPod.Core.Sync;
using PlexPod.Device;
using PlexPod.Device.Sync;

namespace PlexPod.Tests.Device;

/// <summary>A fact that only means something on macOS; skipped elsewhere.</summary>
public sealed class MacFactAttribute : FactAttribute
{
    public MacFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only.";
        }
    }
}

/// <summary>A theory that only means something on macOS; skipped elsewhere.</summary>
public sealed class MacTheoryAttribute : TheoryAttribute
{
    public MacTheoryAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "macOS only.";
        }
    }
}

/// <summary>macOS handling that runs on every OS (pure logic and temp folders).</summary>
public sealed class MacFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public MacFileSystemTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("._01 Glósóli.flac", true)]
    [InlineData("._Music", true)]
    [InlineData(".DS_Store", true)]
    [InlineData(".Spotlight-V100", true)]
    [InlineData(".fseventsd", true)]
    [InlineData(".Trashes", true)]
    [InlineData("01 Glósóli.flac", false)]
    [InlineData(".plexpod", false)]
    [InlineData("08 ...It's Just Wide-Open Field.m4a", false)]
    public void MacMetadata_IsRecognisedByName(string name, bool expected) =>
        Assert.Equal(expected, MacFileSystem.IsMacMetadata(name));

    [Fact]
    public void Listing_OnMac_IsTurnedIntoComposedForm_ElsewhereLeftAlone()
    {
        var decomposed = "/Music/Sigur Rós/01 Glósóli.flac";
        Assert.Equal("/Music/Sigur Rós/01 Glósóli.flac", MacFileSystem.FromListing(decomposed, mac: true));
        Assert.Equal(decomposed, MacFileSystem.FromListing(decomposed, mac: false));
    }

    [Fact]
    public void RemoveTwin_DeletesOnlyTheAppleDoubleTwin_AndOnlyOnMac()
    {
        var file = Path.Combine(_root, "01 a.flac");
        var twin = Path.Combine(_root, "._01 a.flac");
        var other = Path.Combine(_root, "._02 b.flac");
        File.WriteAllText(file, "audio");
        File.WriteAllText(twin, "attrs");
        File.WriteAllText(other, "attrs");

        MacFileSystem.RemoveTwin(file, mac: false);
        Assert.True(File.Exists(twin));

        MacFileSystem.RemoveTwin(file, mac: true);
        Assert.False(File.Exists(twin));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(other));

        var folder = Path.Combine(_root, "Sigur Rós");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(_root, "._Sigur Rós"), "attrs");
        MacFileSystem.RemoveTwin(folder + Path.DirectorySeparatorChar, mac: true);
        Assert.False(File.Exists(Path.Combine(_root, "._Sigur Rós")));
    }

    [Fact]
    public void ListFiles_LeavesOutMacOsOwnFiles()
    {
        foreach (var name in new[] { "Music/A/01 a.flac", "Music/A/._01 a.flac", "Music/._A", "Music/A/.DS_Store" })
        {
            var path = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        Assert.Equal(["/Music/A/01 a.flac"], DeviceFileOps.ListFiles(_root, "Music"));
    }

    // Mac only: the path is resolved with the host's Path.GetFullPath, which on Windows prefixes a drive.
    [MacTheory]
    [InlineData("/Volumes/IPOD/Music/export", "/Volumes/IPOD")]
    [InlineData("/Volumes/IPOD", "/Volumes/IPOD")]
    [InlineData("/Volumes/My iPod/x", "/Volumes/My iPod")]
    [InlineData("/Users/me/Music", "/")]
    public void VolumeRoot_OnMac_IsTheMountUnderVolumes(string path, string root) =>
        Assert.Equal(root, VolumePaths.RootOf(path, windows: false));

    [Theory]
    [InlineData("/Volumes/IPOD", true)]
    [InlineData("/Volumes/IPOD/", true)]
    [InlineData("/", false)]
    [InlineData("/System/Volumes/Data", false)]
    [InlineData("/Volumes/IPOD/inner", false)]
    [InlineData("/Volumes/", false)]
    public void MacDriveSource_OnlyCountsVolumesMounts(string root, bool expected) =>
        Assert.Equal(expected, SystemDriveSource.IsMacVolume(root));

    [MacFact]
    public void NoCache_CanBeTurnedOnForAFile()
    {
        var path = Path.Combine(_root, "f.bin");
        File.WriteAllBytes(path, new byte[8192]);
        using var handle = File.OpenHandle(path);
        Assert.True(MacFileSystem.DisableCache(handle));
    }
}

/// <summary>
/// A real FAT32 volume (a disk image mounted with hdiutil, no admin needed) standing in for the iPod,
/// so macOS's FAT32 driver behaves as it will with the device: decomposed names in listings and
/// <c>._</c> twins for files and folders a process creates.
/// </summary>
public sealed class Fat32Volume : IDisposable
{
    private readonly string _image = string.Empty;

    public Fat32Volume()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var name = "PPT" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        _image = Path.Combine(Path.GetTempPath(), "plexpod-tests", name + ".dmg");
        Directory.CreateDirectory(Path.GetDirectoryName(_image)!);
        Run("hdiutil", "create", "-size", "48m", "-fs", "MS-DOS FAT32", "-volname", name, "-layout", "MBRSPUD", _image);
        Run("hdiutil", "attach", _image, "-nobrowse", "-mountpoint", Path.Combine("/Volumes", name));
        Root = Path.Combine("/Volumes", name);
    }

    public string Root { get; } = string.Empty;

    public void Dispose()
    {
        if (Root.Length > 0)
        {
            Run("hdiutil", "detach", Root, "-force");
            File.Delete(_image);
        }
    }

    private static void Run(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed: {error}");
        }
    }
}

/// <summary>Syncing onto a real FAT32 volume from macOS (M7 plan §3).</summary>
public sealed class MacFat32Tests(Fat32Volume volume) : IClassFixture<Fat32Volume>
{
    private readonly DeviceFileOps _ops = new();

    private string PcFile(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N") + ".bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private IEnumerable<string> AppleDoubleFiles() =>
        Directory.EnumerateFileSystemEntries(volume.Root, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p).StartsWith("._", StringComparison.Ordinal))
            .Where(p => !p.Contains("/.fseventsd", StringComparison.Ordinal) && !p.Contains("/.Spotlight-V100", StringComparison.Ordinal));

    [MacFact]
    public async Task AccentedTrack_IsListedAsWritten_AndLeavesNoTwins()
    {
        var path = "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac";
        Assert.True(path.IsNormalized(NormalizationForm.FormC));

        await _ops.CopyVerifiedAsync(PcFile("audio"), volume.Root, path, CancellationToken.None);

        Assert.Contains(path, DeviceFileOps.ListFiles(volume.Root, "Music"));
        Assert.Empty(AppleDoubleFiles());
    }

    [MacFact]
    public async Task Playlist_WithAccentedLines_VerifiesClean_AndLeavesNoTwins()
    {
        var track = "/Music/Björk/1997 - Homogenic/02 Hunter é.flac";
        await _ops.CopyVerifiedAsync(PcFile("audio"), volume.Root, track, CancellationToken.None);
        var playlist = "/Playlists/Café Mix.m3u8";
        await _ops.WriteTextVerifiedAsync($"#EXTM3U\n{PlaylistWriter.HeaderPrefix}1\n#EXTINF:1,Björk - Hunter é\n{track}\n", volume.Root, playlist, CancellationToken.None);

        Assert.Empty(new PlaylistVerifier().Verify(volume.Root, [playlist]));
        Assert.Empty(AppleDoubleFiles());
    }

    [MacFact]
    public async Task ManifestAndStateFiles_LeaveNoTwins()
    {
        var store = new DeviceStore(volume.Root);
        await store.SaveManifestAsync(DeviceManifest.Empty("dev-1"), CancellationToken.None);
        await store.SaveManifestAsync(DeviceManifest.Empty("dev-1"), CancellationToken.None);
        await store.LoadOrCreateIdentityAsync("IPOD", null, CancellationToken.None);

        Assert.Empty(AppleDoubleFiles());
    }
}
