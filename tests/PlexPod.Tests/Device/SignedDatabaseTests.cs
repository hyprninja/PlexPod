using PlexPod.Core.ItunesDb;
using PlexPod.Device;

namespace PlexPod.Tests.Device;

/// <summary>Stock iPods whose firmware needs a signed database are listed but blocked. Temp directories stand in for drives.</summary>
public sealed class SignedDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plexpod-tests", Guid.NewGuid().ToString("N"));

    public SignedDatabaseTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A restored iPod Video as the owner's looked: empty SysInfo, no SysInfoExtended, no iTunesDB.</summary>
    private string StockDrive(string name = "ipod")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "iPod_Control", "iTunes"));
        Directory.CreateDirectory(Path.Combine(path, "iPod_Control", "Device"));
        File.WriteAllText(Path.Combine(path, "iPod_Control", "Device", "SysInfo"), string.Empty);
        return path;
    }

    private static void Put(string drive, string relative, string text) => File.WriteAllText(Path.Combine(drive, relative), text);

    private static void Put(string drive, string relative, byte[] bytes) => File.WriteAllBytes(Path.Combine(drive, relative), bytes);

    private static IpodDevice Read(string drive, int? usbProductId = null) =>
        IpodLocator.TryRead(new DriveCandidate(drive, "IPOD", 80_000, 79_000), new FakeUsb(usbProductId))!;

    private sealed class FakeUsb(int? productId) : IUsbProductSource
    {
        public int? AppleProductId(string driveRoot) => productId;
    }

    private static string SysInfoExtended(string? dbVersion = null, string? serial = null) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>BuildID</key>
            <string>0x02108000</string>
            {(dbVersion is null ? string.Empty : $"<key>DBVersion</key><integer>{dbVersion}</integer>")}
            <key>FireWireGUID</key>
            <string>000A2700154A3BE6</string>
            <key>FamilyID</key>
            <integer>11</integer>
            {(serial is null ? string.Empty : $"<key>SerialNumber</key><string>{serial}</string>")}
            <key>AudioCodecs</key>
            <dict><key>AAC</key><dict/></dict>
        </dict>
        </plist>
        """;

    /// <summary>The start of an iTunesDB with the given hashing scheme and, optionally, bytes in a hash field.</summary>
    private static byte[] Mhbd(ushort scheme = 0, int hashAt = -1, int headerLength = 0xF4)
    {
        var bytes = new byte[Math.Max(headerLength, 0xF4)];
        "mhbd"u8.CopyTo(bytes);
        BitConverter.GetBytes((uint)headerLength).CopyTo(bytes, 4);
        BitConverter.GetBytes((uint)headerLength).CopyTo(bytes, 8);
        BitConverter.GetBytes(0x75u).CopyTo(bytes, 0x10);
        BitConverter.GetBytes(scheme).CopyTo(bytes, 0x30);
        if (hashAt >= 0)
        {
            bytes[hashAt + 3] = 0x5A;
        }

        return bytes;
    }

    // ---------------------------------------------------------------- allowed

    [Fact]
    public void IpodVideo_WithNoEvidence_IsStockAndAllowed()
    {
        var device = Read(StockDrive());

        Assert.Equal(IpodFirmware.Stock, device.Firmware);
        Assert.Null(device.SignedDatabase);
        Assert.False(device.IsSyncBlocked);
        Assert.True(device.IsSupportedTarget);
    }

    [Fact]
    public void PlexPodsOwnDatabase_IsNotASignature()
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.ItunesDbPath, ItunesDbWriter.Write(new ItdbLibrary(1, 2, "IPOD", 3, [], [])));

        Assert.False(Read(drive).IsSyncBlocked);
    }

    [Theory]
    [InlineData("xA450")] // 80 GB iPod Video 5.5G, black
    [InlineData("MA003")]
    [InlineData("PA444")]
    public void IpodVideoModelNumber_IsAllowed_EvenWithASignedDatabaseLeftBehind(string model)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoPath, $"BoardHwName: iPod Q22\nModelNumStr: {model}\nFirewireGuid: 0x000A27001610F1D5\n");
        Put(drive, SignedDatabaseCheck.ItunesDbPath, Mhbd(scheme: 1, hashAt: 0x58));

        Assert.False(Read(drive).IsSyncBlocked);
    }

    [Fact]
    public void UsbProductIdOfTheIpodVideo_IsAllowed_EvenWithASignedDatabaseLeftBehind()
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.ItunesDbPath, Mhbd(scheme: 1, hashAt: 0x58));

        Assert.False(Read(drive, usbProductId: 0x1209).IsSyncBlocked);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    public void SysInfoExtended_WithAnOldDbVersion_IsAllowed(string version)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, SysInfoExtended(dbVersion: version));

        Assert.False(Read(drive).IsSyncBlocked);
    }

    [Fact]
    public void UnknownModelNumber_IsNoEvidence() =>
        Assert.Null(SignedDatabaseCheck.Classify("ModelNumStr: xZ999\n", null, [], hasNewerLibrary: false, usbProductId: 0x9999));

    // ---------------------------------------------------------------- blocked

    [Theory]
    [InlineData("xB029", "iPod Classic 6G")]
    [InlineData("MB565", "iPod Classic 6.5G")]
    [InlineData("xC297", "iPod Classic 7G")]
    public void ClassicModelNumber_IsBlocked_AndPointsAtRockbox(string model, string name)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoPath, $"ModelNumStr: {model}\n");

        var device = Read(drive);

        Assert.Equal(IpodFirmware.Stock, device.Firmware);
        Assert.True(device.IsSyncBlocked);
        Assert.False(device.IsSupportedTarget);
        Assert.Equal(name, device.SignedDatabase!.Model);
        Assert.Equal(SignedDatabase.NeedsRockboxMessage, device.SignedDatabase.Message);
    }

    [Theory]
    [InlineData("xA978", "iPod nano 3G")]
    [InlineData("MB598", "iPod nano 4G")]
    [InlineData("MC031", "iPod nano 5G")]
    [InlineData("MC525", "iPod nano 6G")]
    public void NanoModelNumber_IsBlocked_WithoutPointingAtRockbox(string model, string name)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoPath, $"ModelNumStr: {model}\n");

        var signed = Read(drive).SignedDatabase!;

        Assert.Equal(name, signed.Model);
        Assert.Equal(SignedDatabase.NoPortMessage, signed.Message);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("5")]
    [InlineData("9")] // unknown and newer: libgpod can't write it either
    public void SysInfoExtended_WithASignedDbVersion_IsBlocked(string version)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, SysInfoExtended(dbVersion: version));

        var signed = Read(drive).SignedDatabase;

        Assert.NotNull(signed);
        Assert.Contains($"DBVersion {version}", signed.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void SysInfoExtended_ClassicSerial_NamesTheModel()
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, SysInfoExtended(dbVersion: "3", serial: "8K7280ABYMV"));

        var signed = Read(drive).SignedDatabase!;

        Assert.Equal("iPod Classic 6G", signed.Model);
        Assert.Equal(SignedDatabase.NeedsRockboxMessage, signed.Message);
    }

    [Fact]
    public void SysInfoExtended_NanoSerialAlone_IsBlocked() =>
        Assert.Equal("iPod nano 4G", SignedDatabaseCheck.Classify(null, SysInfoExtended(serial: "5U8160XY37P"), [], false, null)?.Model);

    [Theory]
    [InlineData(1, -1, "hash58")]
    [InlineData(2, -1, "hash72")]
    [InlineData(3, -1, "hashAB")]
    [InlineData(0, 0x58, "hash58")]
    [InlineData(0, 0x72, "hash72")]
    [InlineData(0, 0xAB, "hashAB")]
    public void SignedItunesDb_WithNoModelEvidence_IsBlocked(int scheme, int hashAt, string hash)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.ItunesDbPath, Mhbd((ushort)scheme, hashAt));

        var signed = Read(drive).SignedDatabase!;

        Assert.Null(signed.Model);
        Assert.Contains(hash, signed.Evidence, StringComparison.Ordinal);
        Assert.Equal(SignedDatabase.NeedsRockboxMessage, signed.Message);
    }

    [Fact]
    public void CompressedDatabase_OfTheNewerNanos_IsBlocked()
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.CompressedDbPath, [0x78, 0x9C]);

        Assert.Equal(SignedDatabase.NoPortMessage, Read(drive).SignedDatabase!.Message);
    }

    [Theory]
    [InlineData(0x1261, "iPod Classic", false)]
    [InlineData(0x1262, "iPod nano 3G", true)]
    [InlineData(0x1263, "iPod nano 4G", true)]
    [InlineData(0x1265, "iPod nano 5G", true)]
    [InlineData(0x1267, "iPod nano 7G", true)]
    public void UsbProductId_OfASignedModel_IsBlocked(int pid, string name, bool noPort)
    {
        var signed = Read(StockDrive(), usbProductId: pid).SignedDatabase!;

        Assert.Equal(name, signed.Model);
        Assert.Equal(noPort, signed.NoRockboxPort);
        Assert.Contains($"0x{pid:X4}", signed.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void SignedModel_BeatsAnUnsignedModelHint()
    {
        // Conflicting evidence errs towards blocking: a wrong block costs a sync, a wrong pass an empty library.
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoPath, "ModelNumStr: xA450\n");

        Assert.True(Read(drive, usbProductId: 0x1261).IsSyncBlocked);
    }

    // ---------------------------------------------------------------- Rockbox, and untrusted input

    [Fact]
    public void RockboxFolder_DecidesRegardlessOfSigningEvidence()
    {
        var drive = StockDrive();
        Directory.CreateDirectory(Path.Combine(drive, ".rockbox"));
        File.WriteAllText(Path.Combine(drive, ".rockbox", "rockbox-info.txt"), "Target: ipodvideo\n");
        Put(drive, SignedDatabaseCheck.SysInfoPath, "ModelNumStr: xB029\n");
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, SysInfoExtended(dbVersion: "3"));
        Put(drive, SignedDatabaseCheck.ItunesDbPath, Mhbd(scheme: 1));

        var device = Read(drive, usbProductId: 0x1261);

        Assert.Equal(IpodFirmware.Rockbox, device.Firmware);
        Assert.Null(device.SignedDatabase);
        Assert.False(device.IsSyncBlocked);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<plist><dict><key>DBVersion</key>")]
    [InlineData("<plist><dict><key>DBVersion</key><integer>three</integer></dict></plist>")]
    [InlineData("<!DOCTYPE plist [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><plist><dict><key>DBVersion</key><integer>&x;</integer></dict></plist>")]
    public void MalformedSysInfoExtended_IsNoEvidence(string text)
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, text);

        Assert.False(Read(drive).IsSyncBlocked);
    }

    [Fact]
    public void TruncatedOrForeignItunesDb_IsNoEvidence()
    {
        Assert.Null(SignedDatabaseCheck.HeaderSignature(Mhbd(scheme: 1)[..0x40]));
        Assert.Null(SignedDatabaseCheck.HeaderSignature(Mhbd(hashAt: 0xAB, headerLength: 0xA0)));
        var notMhbd = Mhbd(scheme: 1);
        notMhbd[0] = (byte)'x';
        Assert.Null(SignedDatabaseCheck.HeaderSignature(notMhbd));

        // A header length beyond the bytes read is clamped, not trusted.
        var lying = Mhbd(hashAt: 0x58, headerLength: 0x70);
        BitConverter.GetBytes(0xFFFF_FFFFu).CopyTo(lying, 4);
        Assert.Equal("hash58", SignedDatabaseCheck.HeaderSignature(lying));
    }

    [Fact]
    public void Detection_WritesNothing()
    {
        var drive = StockDrive();
        Put(drive, SignedDatabaseCheck.SysInfoExtendedPath, SysInfoExtended(dbVersion: "3"));
        Put(drive, SignedDatabaseCheck.ItunesDbPath, Mhbd(scheme: 1));
        var before = Directory.GetFileSystemEntries(drive, "*", SearchOption.AllDirectories).Order().ToList();
        var stamps = before.Select(File.GetLastWriteTimeUtc).ToList();

        _ = Read(drive, usbProductId: 0x1261);

        var after = Directory.GetFileSystemEntries(drive, "*", SearchOption.AllDirectories).Order().ToList();
        Assert.Equal(before, after);
        Assert.Equal(stamps, after.Select(File.GetLastWriteTimeUtc));
    }

    // ---------------------------------------------------------------- USB lookup

    [Fact]
    public void MountedDevice_OfAnIpod_GivesItsSerial() =>
        Assert.Equal("000A27001610F1D5", RegistryUsbProductSource.SerialFromMountedDevice(
            "_??_USBSTOR#Disk&Ven_Apple&Prod_iPod&Rev_1.62#000A27001610F1D5&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}\0"));

    [Theory]
    [InlineData("_??_USBSTOR#Disk&Ven_SanDisk&Prod_Cruzer_Blade&Rev_1.00#00017906110822221539&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData("_??_USBSTOR#Disk&Ven_Apple&Prod_iPod&Rev_1.62#..\\..\\Software&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData("_??_SCSI#Disk&Ven_Apple&Prod_iPod#4&1234&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData("DMIO:ID:\u1234\u5678")]
    [InlineData("")]
    public void MountedDevice_ThatIsntAnAppleUsbDisk_GivesNothing(string path) =>
        Assert.Null(RegistryUsbProductSource.SerialFromMountedDevice(path));

    [Fact]
    public void RegistryLookup_OfAPathThatIsntADriveLetter_GivesNothing()
    {
        var usb = new RegistryUsbProductSource();
        Assert.Null(usb.AppleProductId(@"\\server\share\"));
        Assert.Null(usb.AppleProductId(string.Empty));
    }
}
