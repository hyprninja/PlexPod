using System.Buffers.Binary;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace PlexPod.Device;

/// <summary>
/// Why an iPod's Apple firmware needs a database signed by iTunes, which PlexPod doesn't create.
/// <paramref name="Model"/> is null when only the database on the iPod gave it away.
/// </summary>
public sealed record SignedDatabase(string? Model, string Evidence, bool NoRockboxPort)
{
    public const string NeedsRockboxMessage =
        "This iPod needs Rockbox: its Apple firmware only accepts databases signed by iTunes. See the README's Which iPods work.";

    public const string NoPortMessage =
        "PlexPod can't sync this iPod: its Apple firmware only accepts databases signed by iTunes, and Rockbox doesn't run on it. See the README's Which iPods work.";

    public string Message => NoRockboxPort ? NoPortMessage : NeedsRockboxMessage;
}

/// <summary>
/// Recognises iPods whose Apple firmware only accepts a signed iTunesDB: hash58 from the 2007 iPod
/// Classic and nano 3G/4G, hash72 and hashAB (with an SQLite library) from the nano 5G on. PlexPod writes
/// the unsigned iPod Video database; on those models the firmware would ignore it and show an empty
/// library. The rules follow libgpod (<c>itdb_device.c</c>: the model and serial tables and
/// <c>itdb_device_get_checksum_type</c>).
/// <para>
/// Evidence, strongest first: what the model is (USB product id, <c>SysInfoExtended</c>'s
/// <c>DBVersion</c> and serial, <c>SysInfo</c>'s <c>ModelNumStr</c>), then the database already on
/// the iPod (a hash in the iTunesDB header, or the nano 5G's compressed <c>iTunesCDB</c>). A model known
/// to need no signature wins over database hints. No evidence at all means no signature: the iPod Video
/// has an empty <c>SysInfo</c>, no <c>SysInfoExtended</c> and, freshly restored, no iTunesDB.
/// </para>
/// Everything read from the iPod is untrusted: reads are bounded and a malformed file is no evidence.
/// </summary>
public static class SignedDatabaseCheck
{
    public const string SysInfoPath = "iPod_Control/Device/SysInfo";
    public const string SysInfoExtendedPath = "iPod_Control/Device/SysInfoExtended";
    public const string ItunesDbPath = "iPod_Control/iTunes/iTunesDB";
    public const string CompressedDbPath = "iPod_Control/iTunes/iTunesCDB";
    public const string SqliteLibraryPath = "iPod_Control/iTunes/iTunes Library.itlp";

    private const int MaxSysInfoBytes = 64 * 1024;
    private const int MaxSysInfoExtendedBytes = 1024 * 1024;

    /// <summary>Enough of the mhbd header to reach the end of hashAB (0xAB + 57 bytes).</summary>
    public const int ItunesDbHeadBytes = 0xE4;

    /// <summary>A model family. <paramref name="Name"/> is null when only DBVersion identified it.</summary>
    private sealed record Generation(string? Name, bool Signed, bool NoRockboxPort);

    private static readonly Generation Video = new("iPod Video (5G/5.5G)", Signed: false, NoRockboxPort: false);
    private static readonly Generation OlderIpod = new("iPod (2001-2006)", Signed: false, NoRockboxPort: false);
    private static readonly Generation Classic = new("iPod Classic", Signed: true, NoRockboxPort: false);
    private static readonly Generation Classic6 = new("iPod Classic 6G", Signed: true, NoRockboxPort: false);
    private static readonly Generation Classic65 = new("iPod Classic 6.5G", Signed: true, NoRockboxPort: false);
    private static readonly Generation Classic7 = new("iPod Classic 7G", Signed: true, NoRockboxPort: false);
    private static readonly Generation Nano3 = new("iPod nano 3G", Signed: true, NoRockboxPort: true);
    private static readonly Generation Nano4 = new("iPod nano 4G", Signed: true, NoRockboxPort: true);
    private static readonly Generation Nano5 = new("iPod nano 5G", Signed: true, NoRockboxPort: true);
    private static readonly Generation Nano6 = new("iPod nano 6G", Signed: true, NoRockboxPort: true);
    private static readonly Generation Nano7 = new("iPod nano 7G", Signed: true, NoRockboxPort: true);
    private static readonly Generation UnsignedByVersion = new(null, Signed: false, NoRockboxPort: false);
    private static readonly Generation SignedByVersion = new(null, Signed: true, NoRockboxPort: false);

    /// <summary>Apple model numbers without the leading letter (M, P, ...), as libgpod keys them.</summary>
    private static readonly Dictionary<string, Generation> ModelNumbers = Table(
        (Video, "A002 A146 A003 A147 A452 A444 A446 A664 A448 A450"),
        (Classic6, "B029 B147 B145 B150"),
        (Classic65, "B562 B565"),
        (Classic7, "C293 C297"),
        (Nano3, "A978 A980 B261 B249 B253 B257"),
        (Nano4, "B480 B651 B654 B657 B660 B663 B666 B598 B732 B735 B739 B742 B745 B748 B751 B754 "
            + "B903 B905 B907 B909 B911 B913 B915 B917 B918"),
        (Nano5, "C027 C031 C034 C037 C040 C043 C046 C049 C050 C060 C062 C064 C066 C068 C070 C072 C074 C075"),
        (Nano6, "C525 C688 C689 C690 C691 C692 C693 C526 C694 C695 C696 C697 C698 C699"));

    /// <summary>The last three characters of the serial number → model number (libgpod's serial table).</summary>
    private static readonly Dictionary<string, string> SerialSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SZ9"] = "A002", ["WEC"] = "A002", ["WED"] = "A002", ["WEG"] = "A002", ["WEH"] = "A002", ["WEL"] = "A002",
        ["TXK"] = "A146", ["TXM"] = "A146", ["WEE"] = "A146", ["WEF"] = "A146", ["WEJ"] = "A146", ["WEK"] = "A146",
        ["SZA"] = "A003", ["SZU"] = "A003", ["TXL"] = "A147", ["TXN"] = "A147",
        ["V9K"] = "A444", ["V9L"] = "A444", ["WU9"] = "A444", ["VQM"] = "A446", ["V9M"] = "A446", ["V9N"] = "A446",
        ["V9P"] = "A448", ["V9Q"] = "A448", ["V9R"] = "A450", ["V9S"] = "A450", ["V95"] = "A450", ["V96"] = "A450",
        ["WUC"] = "A450", ["W9G"] = "A664",
        ["Y5N"] = "B029", ["YMV"] = "B147", ["YMU"] = "B145", ["YMX"] = "B150", ["2C5"] = "B562", ["2C7"] = "B565",
        ["9ZS"] = "C293", ["9ZU"] = "C297",
        ["Y0P"] = "A978", ["Y0R"] = "A980", ["YXR"] = "B249", ["YXV"] = "B257", ["YXT"] = "B253", ["YXX"] = "B261",
        ["37P"] = "B663", ["37Q"] = "B666", ["37H"] = "B654", ["1P1"] = "B480", ["37K"] = "B657", ["37L"] = "B660",
        ["2ME"] = "B598", ["3QS"] = "B732", ["3QT"] = "B735", ["3QU"] = "B739", ["3QW"] = "B742", ["3QX"] = "B745",
        ["3QY"] = "B748", ["3R0"] = "B754", ["3QZ"] = "B751",
        ["5B7"] = "B903", ["5B8"] = "B905", ["5B9"] = "B907", ["5BA"] = "B909", ["5BB"] = "B911", ["5BC"] = "B913",
        ["5BD"] = "B915", ["5BE"] = "B917", ["5BF"] = "B918",
        ["71V"] = "C027", ["71Y"] = "C031", ["721"] = "C034", ["726"] = "C037", ["72A"] = "C040", ["72F"] = "C046",
        ["72K"] = "C049", ["72L"] = "C050", ["72Q"] = "C060", ["72R"] = "C062", ["72S"] = "C064", ["72X"] = "C066",
        ["734"] = "C068", ["738"] = "C070", ["739"] = "C072", ["73A"] = "C074", ["73B"] = "C075",
        ["CMN"] = "C525", ["DVX"] = "C688", ["DVY"] = "C689", ["DW0"] = "C690", ["DW1"] = "C691", ["DW2"] = "C692",
        ["DW3"] = "C693", ["CMP"] = "C526", ["DW4"] = "C694", ["DW5"] = "C695", ["DW6"] = "C696", ["DW7"] = "C697",
        ["DW8"] = "C698", ["DW9"] = "C699",
    };

    /// <summary>
    /// Apple's USB product ids for iPods in disk mode. 0x1209 is the iPod Video; 0x1261 to 0x1267 are the
    /// Classic and the nanos from the 3G on (0x1264 isn't used).
    /// </summary>
    private static readonly Dictionary<int, Generation> UsbProductIds = new()
    {
        [0x1201] = OlderIpod, [0x1202] = OlderIpod, [0x1203] = OlderIpod, [0x1204] = OlderIpod, [0x1205] = OlderIpod,
        [0x1206] = OlderIpod, [0x1207] = OlderIpod, [0x1208] = OlderIpod, [0x1209] = Video, [0x120A] = OlderIpod,
        [0x1260] = OlderIpod,
        [0x1261] = Classic, [0x1262] = Nano3, [0x1263] = Nano4, [0x1265] = Nano5, [0x1266] = Nano6, [0x1267] = Nano7,
    };

    /// <summary>Reads the evidence on the drive (read-only, bounded) and classifies it.</summary>
    public static SignedDatabase? Check(string driveRoot, int? usbProductId)
    {
        ArgumentNullException.ThrowIfNull(driveRoot);
        return Classify(
            ReadText(driveRoot, SysInfoPath, MaxSysInfoBytes),
            ReadText(driveRoot, SysInfoExtendedPath, MaxSysInfoExtendedBytes),
            ReadHead(driveRoot, ItunesDbPath, ItunesDbHeadBytes),
            File.Exists(Path.Combine(driveRoot, CompressedDbPath)) || Directory.Exists(Path.Combine(driveRoot, SqliteLibraryPath)),
            usbProductId);
    }

    /// <summary>
    /// Null when nothing says the firmware needs a signed database. <paramref name="itunesDbHead"/> is
    /// the start of the existing iTunesDB (empty if there is none).
    /// </summary>
    public static SignedDatabase? Classify(
        string? sysInfo, string? sysInfoExtended, ReadOnlySpan<byte> itunesDbHead, bool hasNewerLibrary, int? usbProductId)
    {
        // What the model is: any signed model blocks; a known unsigned one overrides the database hints.
        var models = new List<(Generation Generation, string Evidence)>();
        if (usbProductId is { } pid && UsbProductIds.TryGetValue(pid, out var byUsb))
        {
            models.Add((byUsb, $"USB product id 0x{pid:X4}"));
        }

        var extended = ParsePlist(sysInfoExtended);
        if (extended.TryGetValue("DBVersion", out var dbVersion) && long.TryParse(dbVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            // libgpod: 0-2 none, 3 hash58, 4 hash72, 5 hashAB, anything newer unknown (and so not writable).
            models.Add((version >= 3 ? SignedByVersion : UnsignedByVersion, $"SysInfoExtended DBVersion {version}"));
        }

        if (extended.TryGetValue("SerialNumber", out var serial) && FromSerial(serial) is { } bySerial)
        {
            models.Add((bySerial, $"serial number ending {serial.Trim()[^3..]}"));
        }

        if (ParseSysInfo(sysInfo).TryGetValue("ModelNumStr", out var modelNumber) && FromModelNumber(modelNumber) is { } byModel)
        {
            models.Add((byModel, $"model number {modelNumber.Trim()}"));
        }

        // A named model makes a clearer message than DBVersion alone.
        var signed = models.Where(m => m.Generation.Signed).OrderBy(m => m.Generation.Name is null).ToList();
        if (signed.Count > 0)
        {
            var (generation, evidence) = signed[0];
            return new SignedDatabase(generation.Name, evidence, generation.NoRockboxPort);
        }

        if (models.Count > 0)
        {
            return null;
        }

        // Nothing says what the model is: the database iTunes left behind can still give it away.
        if (hasNewerLibrary)
        {
            return new SignedDatabase(null, "an iTunesCDB or SQLite library (nano 5G and later)", NoRockboxPort: true);
        }

        return HeaderSignature(itunesDbHead) is { } hash
            ? new SignedDatabase(null, $"the iTunesDB on the iPod carries a {hash} signature", NoRockboxPort: false)
            : null;
    }

    /// <summary>
    /// The signature an mhbd header carries, or null: the hashing scheme at 0x30 (libgpod's
    /// <c>MhbdHeader</c>), else a non-zero hash58 (0x58), hash72 (0x72) or hashAB (0xAB) field that fits
    /// inside the header. PlexPod's own database leaves them all zero.
    /// </summary>
    internal static string? HeaderSignature(ReadOnlySpan<byte> head)
    {
        if (head.Length < 0x6C || !head[..4].SequenceEqual("mhbd"u8))
        {
            return null;
        }

        var header = head[..(int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(head[4..]), (uint)head.Length)];
        var scheme = header.Length >= 0x32 ? BinaryPrimitives.ReadUInt16LittleEndian(header[0x30..]) : (ushort)0;
        return scheme switch
        {
            1 => "hash58",
            2 => "hash72",
            3 => "hashAB",
            0 when NonZero(header, 0x58, 20) => "hash58",
            0 when NonZero(header, 0x72, 46) => "hash72",
            0 when NonZero(header, 0xAB, 57) => "hashAB",
            0 => null,
            _ => $"type {scheme}",
        };
    }

    private static bool NonZero(ReadOnlySpan<byte> header, int offset, int length) =>
        offset + length <= header.Length && header.Slice(offset, length).ContainsAnyExcept((byte)0);

    private static Generation? FromModelNumber(string value)
    {
        var number = value.Trim();
        if (number.Length > 0 && char.IsAsciiLetter(number[0]))
        {
            number = number[1..];
        }

        return ModelNumbers.GetValueOrDefault(number.ToUpperInvariant());
    }

    private static Generation? FromSerial(string value)
    {
        var serial = value.Trim();
        return serial.Length >= 3 && SerialSuffixes.TryGetValue(serial[^3..], out var model) ? ModelNumbers.GetValueOrDefault(model) : null;
    }

    /// <summary><c>SysInfo</c>: <c>Key: value</c> lines.</summary>
    private static Dictionary<string, string> ParseSysInfo(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (text ?? string.Empty).Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                result.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }

        return result;
    }

    /// <summary>The top-level <c>dict</c> of an XML plist, as key → element text. Malformed → empty.</summary>
    private static Dictionary<string, string> ParsePlist(string? xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xml))
        {
            return result;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaxSysInfoExtendedBytes };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var dict = XDocument.Load(reader).Root?.Element("dict");
            string? key = null;
            foreach (var element in dict?.Elements() ?? [])
            {
                if (element.Name == "key")
                {
                    key = element.Value;
                }
                else if (key is not null)
                {
                    result.TryAdd(key, element.Value);
                    key = null;
                }
            }
        }
        catch (XmlException)
        {
            result.Clear();
        }

        return result;
    }

    private static string? ReadText(string root, string relative, int maxBytes)
    {
        var head = ReadHead(root, relative, maxBytes);
        return head.Length == 0 ? null : System.Text.Encoding.UTF8.GetString(head);
    }

    private static byte[] ReadHead(string root, string relative, int maxBytes)
    {
        var path = Path.Combine(root, relative);
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[(int)Math.Min(maxBytes, stream.Length)];
        stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return buffer;
    }

    private static Dictionary<string, Generation> Table(params (Generation Generation, string Models)[] rows)
    {
        var result = new Dictionary<string, Generation>(StringComparer.Ordinal);
        foreach (var (generation, models) in rows)
        {
            foreach (var model in models.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                result[model] = generation;
            }
        }

        return result;
    }
}
