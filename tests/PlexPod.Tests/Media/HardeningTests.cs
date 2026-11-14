using Microsoft.Extensions.Logging.Abstractions;
using PlexPod.Core.Media;
using PlexPod.Core.Tagging;
using PlexPod.Media;
using PlexPod.Media.Art;
using PlexPod.Media.Ffmpeg;
using PlexPod.Media.Pipeline;

namespace PlexPod.Tests.Media;

public sealed class HardeningTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void ConversionCacheKey_FollowsTheFile_NotPlexMetadata()
    {
        var convert = new FlacAction.Convert(16, 44_100);
        var a = ConversionCache.Key(9001, "/library/parts/9001/1700000000/file.flac", convert);
        Assert.Equal(a, ConversionCache.Key(9001, "/library/parts/9001/1700000000/file.flac", convert));
        Assert.NotEqual(a, ConversionCache.Key(9001, "/library/parts/9001/1800000000/file.flac", convert));
        Assert.DoesNotContain('/', a);
    }

    [Fact]
    public void ConversionCache_NeverOverwritesAnEntryThatAlreadyExists()
    {
        var cache = new ConversionCache(new MediaPaths(_dir.File("cache")));
        var key = ConversionCache.Key(1, "/p/1", new FlacAction.Convert(16, 44_100));
        File.WriteAllText(_dir.File("first.flac"), "first");
        File.WriteAllText(_dir.File("second.flac"), "second");

        var path = cache.Add(key, _dir.File("first.flac"));
        Assert.Equal(path, cache.Add(key, _dir.File("second.flac")));

        Assert.Equal("first", File.ReadAllText(path));
        Assert.False(File.Exists(_dir.File("second.flac")));
    }

    private static byte[] Png(int w, int h) => ImageCodec.EncodePng(new byte[w * h * 3], w, h);

    [Fact]
    public void HugeArt_IsRejectedBeforeAllocating()
    {
        var huge = Png(ArtProcessor.MaxSourceSide + 1, 1);
        Assert.Throws<InvalidDataException>(() => ArtProcessor.Process(huge));
    }

    [Fact]
    public async Task ArtCacheFileNames_NeverComeFromServerData()
    {
        var paths = new MediaPaths(_dir.File("cache"));
        var provider = new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance);
        var client = new FakeLibraryClient { Image = TagWriterTests.Jpeg(400, 400) };
        var album = new AlbumDetail(@"..\..\..\escaped", "A", "ar", "Artist", null, null, [], [], null, 1, "/thumb/1");

        Assert.NotNull(await provider.GetAsync(client, album, CancellationToken.None));

        Assert.Equal(2, Directory.GetFiles(paths.ArtCache).Length);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*escaped*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ArtThatCantBeDecoded_NeverThrows_TheTrackJustHasNoCover()
    {
        var paths = new MediaPaths(_dir.File("cache"));
        var provider = new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance)
        {
            Processor = _ => throw new IOException("Cannot read from the stream."),
        };
        var client = new FakeLibraryClient { Image = [1, 2, 3] };
        var album = new AlbumDetail("1", "We Can't Dance", "ar", "Genesis", 1991, null, [], [], null, 1, "/thumb/1");

        Assert.Null(await provider.GetAsync(client, album, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task ArtTheWindowsDecoderRejects_IsRetriedThroughFfmpeg()
    {
        var paths = new MediaPaths(_dir.File("cache"));
        var calls = 0;
        var provider = new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance, TestFfmpeg.Runner)
        {
            // First attempt (the original bytes) fails as Windows did for the owner's posters; the PNG from ffmpeg works.
            Processor = bytes => ++calls == 1 ? throw new IOException("Cannot read from the stream.") : ArtProcessor.Process(bytes),
        };
        var client = new FakeLibraryClient { Image = TagWriterTests.Jpeg(640, 480) };
        var album = new AlbumDetail("1", "We Can't Dance", "ar", "Genesis", 1991, null, [], [], null, 1, "/thumb/1");

        var images = await provider.GetAsync(client, album, CancellationToken.None, await TestFfmpeg.InstallAsync());

        Assert.NotNull(images);
        Assert.Equal(2, calls);
        Assert.True(ArtProcessor.IsBaselineJpeg(images.CoverJpeg));
    }

    [Fact]
    public void FfmpegSearch_IgnoresRelativePathEntries()
    {
        var candidates = OperatingSystem.IsWindows()
            ? FfmpegLocator.Candidates(null, @".;bin;""C:\Tools\ffmpeg\bin""").ToList()
            : FfmpegLocator.Candidates(null, @".:bin:""/Users/me/tools/ffmpeg""").ToList();
        Assert.Contains(OperatingSystem.IsWindows() ? @"C:\Tools\ffmpeg\bin\ffmpeg.exe" : "/Users/me/tools/ffmpeg/ffmpeg", candidates);
        Assert.DoesNotContain(candidates, c => !Path.IsPathFullyQualified(c));
    }

    [Fact]
    public void FfmpegSearch_OnMac_TriesHomebrewAfterPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var candidates = FfmpegLocator.Candidates(null, "/usr/bin").ToList();
        Assert.Equal(["/usr/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/local/bin/ffmpeg"], candidates);
    }

    [Fact]
    public void OperationGate_LetsOneOperationRunAtATime()
    {
        var gate = new OperationGate();
        var changes = 0;
        gate.Changed += (_, _) => changes++;

        using (var sync = gate.TryEnter("Sync"))
        {
            Assert.NotNull(sync);
            Assert.Null(gate.TryEnter("Test export"));
            Assert.Equal("Sync", gate.Holder);
        }

        Assert.True(gate.IsFree);
        using var export = gate.TryEnter("Test export");
        Assert.NotNull(export);
        Assert.Equal(3, changes);
    }
}
