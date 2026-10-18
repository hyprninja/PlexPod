using PlexPod.Device;

namespace PlexPod.Tests.Device;

/// <summary>Temp directories stand in for drives.</summary>
public sealed class DeviceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public DeviceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string FakeDrive(string name, bool rockbox, string? info = null, bool stock = false)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        if (stock)
        {
            Directory.CreateDirectory(Path.Combine(path, "iPod_Control", "iTunes"));
            Directory.CreateDirectory(Path.Combine(path, "iPod_Control", "Device"));
        }

        if (rockbox)
        {
            Directory.CreateDirectory(Path.Combine(path, ".rockbox"));
            if (info is not null)
            {
                File.WriteAllText(Path.Combine(path, ".rockbox", "rockbox-info.txt"), info);
            }
        }

        return path;
    }

    private sealed class FakeDrives : IDriveSource
    {
        public List<DriveCandidate> Drives { get; } = [];

        public IEnumerable<DriveCandidate> GetDrives() => Drives;
    }

    private const string RealisticInfo = """
        Target: ipodvideo
        Target id: 26
        Target define: IPOD_VIDEO
        Memory: 64
        CPU: PP5022
        Manufacturer: apple
        Version: v4.0-251018
        Binary: rockbox.ipod
        """;

    [Fact]
    public void DriveWithRockboxFolder_IsDetected_WithInfo()
    {
        var drives = new FakeDrives();
        drives.Drives.Add(new DriveCandidate(FakeDrive("ipod", true, RealisticInfo), "IPOD", 80_000, 60_000));
        drives.Drives.Add(new DriveCandidate(FakeDrive("usbstick", false), "STICK", 1, 1));

        var device = Assert.Single(new IpodLocator(drives).FindDevices());

        Assert.Equal("IPOD", device.VolumeLabel);
        Assert.Equal("ipodvideo", device.RockboxTarget);
        Assert.Equal("v4.0-251018", device.RockboxVersion);
        Assert.True(device.IsSupportedTarget);
        Assert.Equal(60_000, device.Capacity.FreeBytes);
    }

    [Fact]
    public void MissingInfoFile_IsFine_ButTargetIsUnsupported()
    {
        var device = IpodLocator.TryRead(new DriveCandidate(FakeDrive("ipod", true), "IPOD", 1, 1));
        Assert.NotNull(device);
        Assert.Null(device.RockboxTarget);
        Assert.False(device.IsSupportedTarget);
    }

    [Fact]
    public void StockIpod_IsDetectedAsStock()
    {
        var device = IpodLocator.TryRead(new DriveCandidate(FakeDrive("stock", rockbox: false, stock: true), "IPOD", 80_000, 79_000));
        Assert.NotNull(device);
        Assert.Equal(IpodFirmware.Stock, device.Firmware);
        Assert.True(device.IsSupportedTarget);
        Assert.Null(device.RockboxTarget);
    }

    [Fact]
    public void RockboxInstalledOverStockFirmware_IsRockbox()
    {
        var device = IpodLocator.TryRead(new DriveCandidate(FakeDrive("both", rockbox: true, RealisticInfo, stock: true), "IPOD", 1, 1));
        Assert.Equal(IpodFirmware.Rockbox, device!.Firmware);
    }

    [Fact]
    public void HalfAnIpodControlFolder_IsNotAnIpod()
    {
        var path = FakeDrive("odd", rockbox: false);
        Directory.CreateDirectory(Path.Combine(path, "iPod_Control"));
        Assert.Null(IpodLocator.TryRead(new DriveCandidate(path, "X", 1, 1)));
    }

    [Fact]
    public void DriveWithoutRockbox_IsIgnored() =>
        Assert.Null(IpodLocator.TryRead(new DriveCandidate(FakeDrive("plain", false), "X", 1, 1)));

    [Fact]
    public void Detection_WritesNothing()
    {
        var path = FakeDrive("ipod", true, RealisticInfo);
        var before = Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Order().ToList();
        _ = IpodLocator.TryRead(new DriveCandidate(path, "IPOD", 1, 1));
        Assert.Equal(before, Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Order());
    }

    [Fact]
    public void Parser_ReadsRealisticFile_CaseInsensitive()
    {
        var info = RockboxInfoParser.Parse(RealisticInfo.Replace("\n", "\r\n", StringComparison.Ordinal));
        Assert.Equal("ipodvideo", info.Target);
        Assert.Equal("64", info.Memory);
        Assert.Equal("IPOD_VIDEO", info.Values["target DEFINE"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n\n")]
    public void Parser_EmptyInput_IsEmpty(string? text) => Assert.Empty(RockboxInfoParser.Parse(text).Values);

    [Fact]
    public void Parser_SkipsJunk_FirstValueWins_KeepsColonsInValues()
    {
        var info = RockboxInfoParser.Parse("﻿garbage line\n: no key\nTarget: ipodvideo\nTarget: ipodnano\nVersion: 4.0 build: 12:34\n\0\0");
        Assert.Equal("ipodvideo", info.Target);
        Assert.Equal("4.0 build: 12:34", info.Version);
        Assert.Equal(2, info.Values.Count);
    }

    [Fact]
    public void Watcher_RaisesArrivedChangedAndRemoved()
    {
        var drives = new FakeDrives();
        var watcher = new DeviceWatcher(new IpodLocator(drives), TimeProvider.System);
        var events = new List<string>();
        watcher.DeviceArrived += (_, e) => events.Add("arrived " + e.Device.VolumeLabel);
        watcher.DeviceChanged += (_, e) => events.Add("changed " + e.Device.FreeBytes);
        watcher.DeviceRemoved += (_, e) => events.Add("removed " + e.Device.VolumeLabel);

        watcher.Poll();
        var path = FakeDrive("ipod", true, RealisticInfo);
        drives.Drives.Add(new DriveCandidate(path, "IPOD", 100, 50));
        watcher.Poll();
        watcher.Poll();
        drives.Drives[0] = drives.Drives[0] with { FreeBytes = 40 };
        watcher.Poll();
        Assert.Single(watcher.Devices);
        drives.Drives.Clear();
        watcher.Poll();

        Assert.Equal(["arrived IPOD", "changed 40", "removed IPOD"], events);
        Assert.Empty(watcher.Devices);
    }

    [Fact]
    public async Task Watcher_Start_NeverScansOnTheCallingContext()
    {
        // A slow drive scan must not run on the caller's (UI) context.
        var context = new RecordingContext();
        var scanThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var locator = new SlowLocator(() => scanThread.TrySetResult(Environment.CurrentManagedThreadId));
        await using var watcher = new DeviceWatcher(locator, TimeProvider.System) { Interval = TimeSpan.FromSeconds(10) };

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            watcher.Start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await scanThread.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.Posts);
    }

    private sealed class RecordingContext : SynchronizationContext
    {
        public int Posts { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            Posts++;
            base.Post(d, state);
        }
    }

    private sealed class SlowLocator(Action onScan) : IIpodLocator
    {
        public IReadOnlyList<IpodDevice> FindDevices()
        {
            onScan();
            return [];
        }
    }

    [Fact]
    public async Task Watcher_StartPollsInBackground()
    {
        var drives = new FakeDrives();
        drives.Drives.Add(new DriveCandidate(FakeDrive("ipod", true, RealisticInfo), "IPOD", 100, 50));
        await using var watcher = new DeviceWatcher(new IpodLocator(drives), TimeProvider.System) { Interval = TimeSpan.FromMilliseconds(20) };
        var arrived = new TaskCompletionSource<IpodDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.DeviceArrived += (_, e) => arrived.TrySetResult(e.Device);

        watcher.Start();
        var device = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("IPOD", device.VolumeLabel);
    }
}
