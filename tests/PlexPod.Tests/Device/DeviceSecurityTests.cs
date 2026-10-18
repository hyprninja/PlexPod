using PlexPod.Device.Sync;

namespace PlexPod.Tests.Device;

/// <summary>Paths read from the iPod (manifest, playlists) are untrusted: they must never reach outside the device.</summary>
public sealed class DeviceSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public DeviceSecurityTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("/C:/Users/victim/important.docx")]
    [InlineData("/Music/../../Windows/win.ini")]
    [InlineData("/Music/a\\..\\..\\x")]
    [InlineData("/Music/./a.flac")]
    [InlineData("/Music//a.flac")]
    [InlineData("Music/a.flac")]
    [InlineData("/")]
    [InlineData("/Music/a\u0000.flac")]
    public void UnsafeDevicePaths_AreRejected(string devicePath)
    {
        Assert.False(DevicePaths.IsWellFormed(devicePath));
        Assert.Throws<ArgumentException>(() => DevicePaths.ToLocal(_root, devicePath));
    }

    [Theory]
    [InlineData("/Music/Cloudkicker/2010 - Beacons/08 ...It's Just Wide-Open Field.m4a")]
    [InlineData("/Music/Metallica/1988 - ...And Justice for All/01 Blackened.flac")]
    [InlineData("/Music/A/..Leading dots.flac")]
    [InlineData("/Playlists/Mix...Tape.m3u8")]
    public void DotsInsideNames_AreFine(string devicePath)
    {
        Assert.True(DevicePaths.IsManaged(devicePath));
        Assert.EndsWith(Path.GetFileName(devicePath.Replace('/', Path.DirectorySeparatorChar)), DevicePaths.ToLocal(_root, devicePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Music/Trailing dot./a.flac")]
    [InlineData("/Music/a.flac ")]
    [InlineData("/Music/.../a.flac")]
    public void SegmentsWindowsWouldAlter_AreRejected(string devicePath) =>
        Assert.False(DevicePaths.IsWellFormed(devicePath));

    [Fact]
    public void SafePaths_StayInsideTheDrive()
    {
        var local = DevicePaths.ToLocal(_root, "/Music/Sigur Rós/2005 - Takk/02 Hoppípolla.flac");
        Assert.StartsWith(Path.GetFullPath(_root), local, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/Music/a.flac", true)]
    [InlineData("/playlists/x.m3u8", true)]
    [InlineData("/.rockbox/rockbox.ipod", false)]
    [InlineData("/.plexpod/manifest.json", false)]
    [InlineData("/Music", false)]
    [InlineData("/Photos/a.jpg", false)]
    public void OnlyMusicAndPlaylists_AreManaged(string devicePath, bool managed) =>
        Assert.Equal(managed, DevicePaths.IsManaged(devicePath));

    [Fact]
    public async Task FileOps_RefuseToTouchAnythingOutsideMusicAndPlaylists()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".rockbox"));
        var firmware = Path.Combine(_root, ".rockbox", "rockbox.ipod");
        File.WriteAllText(firmware, "firmware");
        var pcFile = Path.Combine(_root, "source.bin");
        File.WriteAllText(pcFile, "x");
        var ops = new DeviceFileOps();

        Assert.Throws<InvalidOperationException>(() => DeviceFileOps.Delete(_root, "/.rockbox/rockbox.ipod"));
        Assert.Throws<InvalidOperationException>(() => ops.Move(_root, "/.rockbox/rockbox.ipod", "/Music/x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CopyVerifiedAsync(pcFile, _root, "/.rockbox/rockbox.ipod", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.WriteTextVerifiedAsync("x", _root, "/.rockbox/config.cfg", CancellationToken.None));
        Assert.Equal("firmware", File.ReadAllText(firmware));
    }

    [Fact]
    public void EmptyFolderCleanup_IgnoresFoldersOutsideTheManagedRoots()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".rockbox", "empty"));
        Directory.CreateDirectory(Path.Combine(_root, "Music"));
        var removed = DeviceFileOps.DeleteEmptyFolders(_root, ["/.rockbox/empty", "/.rockbox", "/Music"]);
        Assert.Empty(removed);
        Assert.True(Directory.Exists(Path.Combine(_root, ".rockbox", "empty")));
    }

    [Fact]
    public void PlaylistCheck_NeverFollowsDotDotOrDriveLetters()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Playlists"));
        File.WriteAllText(Path.Combine(_root, "Playlists", "x.m3u8"), "/Music/../../Windows/win.ini\n/C:/Windows/win.ini\n");
        var problems = new PlaylistVerifier().Verify(_root, ["/Playlists/x.m3u8"]);
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Equal("not a valid device path", p.Reason));
    }
}
