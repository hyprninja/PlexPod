using Microsoft.Extensions.Logging.Abstractions;
using RockPlex.Core.Media;
using RockPlex.Core.Models;
using RockPlex.Core.Tagging;
using RockPlex.Media;
using RockPlex.Media.Art;
using RockPlex.Media.Ffmpeg;
using RockPlex.Media.Pipeline;
using RockPlex.Media.Tags;

namespace RockPlex.Tests.Media;

/// <summary>End-to-end checks with the real ffmpeg (skipped when it isn't installed).</summary>
public sealed class PipelineTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FlacConverter _converter = new(TestFfmpeg.Runner);
    private readonly ContainerCleaner _cleaner = new(TestFfmpeg.Runner);
    private readonly AudioVerifier _verifier = new(TestFfmpeg.Runner);

    public void Dispose() => _dir.Dispose();

    private TrackPipeline Pipeline(out ConversionCache cache)
    {
        var paths = new MediaPaths(_dir.File("cache"));
        cache = new ConversionCache(paths);
        return new TrackPipeline(paths, cache, _converter, _cleaner, _verifier, NullLogger<TrackPipeline>.Instance);
    }

    private static readonly AlbumDetail Album = new("al1", "Takk...", "ar1", "Sigur Rós", 2005, "2005-09-12", ["Post-Rock"], [], "EMI", 2, "/thumb/1");

    private static TrackItem Track(string key, long partId, string ext, int index = 1, string? title = null) =>
        new(key, $"plex://track/{key}", title ?? $"Song {key}", "al1", "ar1", null, index, 1, 1000,
            new AudioFormat(ext, ext), [new MediaPartRef(partId, $"/library/parts/{partId}/1/file.{ext}", 0, ext)], 1700000000);

    private TrackJob Job(TrackItem track, string output) =>
        new(track, Album, null, [track], null, null, TagPolicy.Default, output);

    [FfmpegFact]
    public async Task HiRes_24_96_IsConvertedTo_16_44_AndVerified()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("hires.flac"), 96_000, 24);
        var info = FlacFile.ReadStreamInfo(src);
        Assert.Equal((24, 96_000), (info.BitsPerSample, info.SampleRateHz));

        var action = Assert.IsType<FlacAction.Convert>(FinalFormatPolicy.Decide(info));
        var output = _dir.File("out.flac");
        await _converter.ConvertAsync(ffmpeg, src, output, info, action, CancellationToken.None);

        var result = FlacFile.ReadStreamInfo(output);
        Assert.Equal((16, 44_100), (result.BitsPerSample, result.SampleRateHz));
        Assert.True(result.MaxBlockSize <= FinalFormatPolicy.OutputBlockSize);
        await _verifier.VerifyConversionAsync(ffmpeg, info, output, action, CancellationToken.None);
        Assert.True(new FileInfo(output).Length < new FileInfo(src).Length);
    }

    [FfmpegFact]
    public async Task HiBit_24_32_KeepsItsRate()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("in.flac"), 32_000, 24);
        var info = FlacFile.ReadStreamInfo(src);
        var action = Assert.IsType<FlacAction.Convert>(FinalFormatPolicy.Decide(info));
        Assert.Equal(new FlacAction.Convert(16, 32_000), action);

        var output = _dir.File("out.flac");
        await _converter.ConvertAsync(ffmpeg, src, output, info, action, CancellationToken.None);
        Assert.Equal((16, 32_000), (FlacFile.ReadStreamInfo(output).BitsPerSample, FlacFile.ReadStreamInfo(output).SampleRateHz));
        await _verifier.VerifyConversionAsync(ffmpeg, info, output, action, CancellationToken.None);
    }

    [FfmpegFact]
    public async Task Verifier_RejectsAnOutputThatMissesItsTarget()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("in.flac"), 96_000, 24);
        var info = FlacFile.ReadStreamInfo(src);
        var output = _dir.File("out.flac");
        await _converter.ConvertAsync(ffmpeg, src, output, info, new FlacAction.Convert(16, 44_100), CancellationToken.None);

        await Assert.ThrowsAsync<VerificationException>(() =>
            _verifier.VerifyConversionAsync(ffmpeg, info, output, new FlacAction.Convert(16, 48_000), CancellationToken.None));

        // Pretending the source was lower than the output must trip the never-upsample check.
        await Assert.ThrowsAsync<VerificationException>(() =>
            _verifier.VerifyConversionAsync(ffmpeg, info with { SampleRateHz = 32_000 }, output, new FlacAction.Convert(16, 44_100), CancellationToken.None));
    }

    [FfmpegFact]
    public async Task CdQuality_IsCopiedBitPerfect_WithCleanTags()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("cd.flac"), 44_100, 16);
        var client = new FakeLibraryClient();
        client.PartFiles[1] = src;
        var output = _dir.File("export/02 Song.flac");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t1", 1, "flac"), output), CancellationToken.None);

        Assert.StartsWith("copied (FLAC 16/44.1)", result.Action, StringComparison.Ordinal);
        Assert.Equal(TagWriteOutcome.Retagged, result.Tagging);
        Assert.Equal(await _verifier.PacketMd5Async(ffmpeg, src, CancellationToken.None), await _verifier.PacketMd5Async(ffmpeg, output, CancellationToken.None));
        Assert.Equal(FlacFile.ReadStreamInfo(src).AudioMd5, FlacFile.ReadStreamInfo(output).AudioMd5);
        Assert.Contains(TagWriter.ReadBack(output).Fields, f => f is { Key: "ALBUM", Value: "Takk..." });
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir.File("cache/work")));
    }

    [FfmpegFact]
    public async Task FlacWithTrailingId3v1_IsCopied_WithoutAFalseAudioChangeAlarm()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("tagged.flac"), 44_100, 16);

        // A 128-byte ID3v1 tag stuck on the end, as old taggers did.
        var id3v1 = new byte[128];
        "TAG"u8.CopyTo(id3v1);
        "Old Junk Title"u8.CopyTo(id3v1.AsSpan(3));
        File.WriteAllBytes(src, [.. File.ReadAllBytes(src), .. id3v1]);

        var client = new FakeLibraryClient();
        client.PartFiles[1] = src;
        var output = _dir.File("tagged-out.flac");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t1", 1, "flac"), output), CancellationToken.None);

        Assert.StartsWith("copied", result.Action, StringComparison.Ordinal);
        Assert.Equal(await _verifier.DecodedMd5Async(ffmpeg, src, CancellationToken.None), await _verifier.DecodedMd5Async(ffmpeg, output, CancellationToken.None));
        Assert.Equal(TagLib.TagTypes.None, TagWriter.ReadBack(output).ForeignTags);
    }

    [FfmpegFact]
    public async Task M4a_IsRebuiltClean_KeepsGaplessInfo_AndAudioPackets()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = _dir.CopyFixture("tone.m4a");
        using (var file = TagLib.File.Create(src))
        {
            var apple = (TagLib.Mpeg4.AppleTag)file.GetTag(TagLib.TagTypes.Apple, true);
            apple.Title = "Old Title";
            apple.Year = 1999;
            apple.Comment = "ripped by junk";
            apple.SetDashBox("com.apple.iTunes", "iTunSMPB", " 00000000 00000840 000001C0 0000000000003A00");
            apple.SetDashBox("com.apple.iTunes", "Some Junk", "zzz");
            file.Save();
        }

        var client = new FakeLibraryClient();
        client.PartFiles[5] = src;
        var output = _dir.File("clean.m4a");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t5", 5, "m4a", 1, "New Title"), output), CancellationToken.None);

        Assert.Equal("copied (m4a)", result.Action);
        Assert.Equal(await _verifier.PacketMd5Async(ffmpeg, src, CancellationToken.None), await _verifier.PacketMd5Async(ffmpeg, output, CancellationToken.None));
        var back = TagWriter.ReadBack(output);
        Assert.Contains(back.Fields, f => f is { Key: "TITLE", Value: "New Title" });
        Assert.DoesNotContain(back.Fields, f => f.Value is "Old Title" or "ripped by junk");
        using var check = TagLib.File.Create(output);
        var tag = (TagLib.Mpeg4.AppleTag)check.GetTag(TagLib.TagTypes.Apple);
        Assert.Equal(" 00000000 00000840 000001C0 0000000000003A00", tag.GetDashBox("com.apple.iTunes", "iTunSMPB"));
        Assert.Null(tag.GetDashBox("com.apple.iTunes", "Some Junk"));
    }

    [FfmpegFact]
    public async Task FailedTrack_KeepsItsOriginal_ForInspection()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = _dir.File("broken.flac");
        File.WriteAllBytes(src, [.. "fLaC"u8, 0x80, 0, 0, 34, .. new byte[34], .. new byte[64]]); // STREAMINFO with rate 0
        var client = new FakeLibraryClient();
        client.PartFiles[9] = src;
        var failed = _dir.File("failed");

        await Assert.ThrowsAsync<PipelineException>(() => Pipeline(out _).ProcessAsync(client, ffmpeg,
            Job(Track("t9", 9, "flac"), _dir.File("never.flac")) with { FailedSourceFolder = failed }, CancellationToken.None));

        Assert.Single(Directory.GetFiles(failed));
        Assert.False(File.Exists(_dir.File("never.flac")));
    }

    [FfmpegFact]
    public async Task LowRate_IsCopied_NeverUpsampled()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("low.flac"), 22_050, 16);
        var client = new FakeLibraryClient();
        client.PartFiles[1] = src;
        var output = _dir.File("low-out.flac");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t1", 1, "flac"), output), CancellationToken.None);

        Assert.StartsWith("copied", result.Action, StringComparison.Ordinal);
        Assert.Equal(22_050, FlacFile.ReadStreamInfo(output).SampleRateHz);
    }

    [FfmpegFact]
    public async Task LargeBlocks_AreReencodedLosslessly_WithIdenticalAudio()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("big.flac"), 44_100, 16, blockSize: 8192);
        Assert.Equal(8192, FlacFile.ReadStreamInfo(src).MaxBlockSize);
        var client = new FakeLibraryClient();
        client.PartFiles[1] = src;
        var output = _dir.File("big-out.flac");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t1", 1, "flac"), output), CancellationToken.None);

        Assert.StartsWith("re-encoded losslessly", result.Action, StringComparison.Ordinal);
        Assert.True(FlacFile.ReadStreamInfo(output).MaxBlockSize <= FinalFormatPolicy.MaxSafeBlockSize);
        Assert.Equal(await _verifier.DecodedMd5Async(ffmpeg, src, CancellationToken.None), await _verifier.DecodedMd5Async(ffmpeg, output, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task Conversion_IsCached_SoATagChangeNeverReconverts()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("hires.flac"), 96_000, 24);
        var client = new FakeLibraryClient();
        client.PartFiles[7] = src;
        var pipeline = Pipeline(out var cache);
        var track = Track("t1", 7, "flac");

        var first = await pipeline.ProcessAsync(client, ffmpeg, Job(track, _dir.File("a.flac")), CancellationToken.None);
        var second = await pipeline.ProcessAsync(client, ffmpeg, Job(track with { Title = "Renamed in Plex" }, _dir.File("b.flac")), CancellationToken.None);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal("converted 24/96 → 16/44.1", second.Action);
        Assert.NotEqual(first.TagHash, second.TagHash);
        Assert.Single(Directory.GetFiles(_dir.File("cache/converted")));
        Assert.Contains(TagWriter.ReadBack(_dir.File("b.flac")).Fields, f => f is { Key: "TITLE", Value: "Renamed in Plex" });
    }

    [FfmpegFact]
    public async Task Mp3_IsCopied_FramesUntouched()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = _dir.CopyFixture("tone.mp3");
        var client = new FakeLibraryClient();
        client.PartFiles[3] = src;
        var output = _dir.File("song.mp3");

        var result = await Pipeline(out _).ProcessAsync(client, ffmpeg, Job(Track("t3", 3, "mp3"), output), CancellationToken.None);

        Assert.Equal("copied (mp3)", result.Action);
        Assert.Equal(await _verifier.PacketMd5Async(ffmpeg, src, CancellationToken.None), await _verifier.PacketMd5Async(ffmpeg, output, CancellationToken.None));
    }

    [FfmpegFact]
    public async Task Cancel_KillsFfmpeg_AndLeavesNoOutput()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var src = await TestFfmpeg.MakeFlacAsync(_dir.File("long.flac"), 192_000, 24, seconds: 180);
        var info = FlacFile.ReadStreamInfo(src);
        var output = _dir.File("never.flac");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _converter.ConvertAsync(ffmpeg, src, output, info, new FlacAction.Convert(16, 44_100), cts.Token));

        // ffmpeg is gone, so the partial file can be deleted, and the pipeline would never have used it.
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        Assert.False(File.Exists(output));
    }

    [FfmpegFact]
    public async Task TestExport_WritesTracksCoverPlaylistAndReport()
    {
        var ffmpeg = await TestFfmpeg.InstallAsync();
        var client = new FakeLibraryClient { Image = TagWriterTests.Jpeg(600, 600) };
        client.PartFiles[1] = await TestFfmpeg.MakeFlacAsync(_dir.File("src1.flac"), 96_000, 24);
        client.PartFiles[2] = _dir.CopyFixture("tone.mp3");
        client.Albums["al1"] = Album;

        var t1 = Track("t1", 1, "flac", 1, "Glósóli");
        var t2 = Track("t2", 2, "mp3", 2, "What? Now");
        var gone = Track("t3", 3, "flac", 3) with { Parts = [] };
        var snapshot = new LibrarySnapshot("3", [new ArtistItem("ar1", "g", "Sigur Rós")],
            [new AlbumItem("al1", "g", "Takk...", "ar1", "Sigur Rós", 2005, 3)], [t1, t2, gone], []);
        var playlist = new PlaylistItem("p1", "g", "Road/Trip", false, 4, 0, "/playlists/p1/items");
        var contents = new PlaylistContents("p1", [new(1, t2, false), new(2, t1, false), new(3, t2, false), new(4, gone, true)]);

        var paths = new MediaPaths(_dir.File("cache"));
        var service = new TestExportService(
            new TrackPipeline(paths, new ConversionCache(paths), _converter, _cleaner, _verifier, NullLogger<TrackPipeline>.Instance),
            new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance),
            NullLogger<TestExportService>.Instance);
        var folder = _dir.File("export");

        var report = await service.ExportAsync(client, ffmpeg,
            new ExportRequest(folder, snapshot, [t1, t2, gone], [(playlist, contents)], TagPolicy.Default with { MusicBrainzTrackId = false }),
            null, CancellationToken.None);

        Assert.Equal(2, report.Succeeded);
        Assert.Equal(0, report.Failed);
        var albumDir = Path.Combine(folder, "Music", "Sigur Rós", "2005 - Takk");
        Assert.True(File.Exists(Path.Combine(albumDir, "01 Glósóli.flac")));
        Assert.True(File.Exists(Path.Combine(albumDir, "02 What_ Now.mp3")));
        Assert.True(ArtProcessor.IsBaselineJpeg(File.ReadAllBytes(Path.Combine(albumDir, "cover.jpg"))));
        Assert.Equal((16, 44_100), (FlacFile.ReadStreamInfo(Path.Combine(albumDir, "01 Glósóli.flac")).BitsPerSample, FlacFile.ReadStreamInfo(Path.Combine(albumDir, "01 Glósóli.flac")).SampleRateHz));
        Assert.Equal(1, TagWriter.ReadBack(Path.Combine(albumDir, "02 What_ Now.mp3")).PictureCount);

        var m3u = File.ReadAllLines(Path.Combine(folder, "Playlists", "Road_Trip.m3u8"));
        var paths3 = m3u.Where(l => l.StartsWith('/')).ToList();
        Assert.Equal(["/Music/Sigur Rós/2005 - Takk/02 What_ Now.mp3", "/Music/Sigur Rós/2005 - Takk/01 Glósóli.flac", "/Music/Sigur Rós/2005 - Takk/02 What_ Now.mp3"], paths3);
        Assert.Contains(m3u, l => l.StartsWith("# not exported", StringComparison.Ordinal));

        var text = File.ReadAllText(report.ReportPath);
        Assert.Contains("converted 24/96 → 16/44.1", text, StringComparison.Ordinal);
        Assert.Contains("Tags stripped: ", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Directory.EnumerateFiles(folder, "*.rockplex-tmp", SearchOption.AllDirectories), _ => true);
    }

    [Fact]
    public void RockboxDrive_IsDetectedFromAnyFolderOnIt()
    {
        static bool IpodOnX(string p) => string.Equals(p, @"X:\.rockbox", StringComparison.OrdinalIgnoreCase);
        Assert.True(TestExportService.IsOnRockboxDrive(@"X:\Music\export", IpodOnX));
        Assert.True(TestExportService.IsOnRockboxDrive(@"X:\", IpodOnX));
        Assert.False(TestExportService.IsOnRockboxDrive(@"C:\Users\me\export", IpodOnX));
    }

    [Fact]
    public async Task Export_ToARockboxDrive_IsRefusedBeforeAnythingIsWritten()
    {
        var root = Path.GetPathRoot(_dir.Path)!;
        if (!Directory.Exists(Path.Combine(root, ".rockbox")))
        {
            // Only meaningful where the temp drive is a Rockbox drive; the detection itself is covered above.
            return;
        }

        var paths = new MediaPaths(_dir.File("cache"));
        var service = new TestExportService(
            new TrackPipeline(paths, new ConversionCache(paths), _converter, _cleaner, _verifier, NullLogger<TrackPipeline>.Instance),
            new AlbumArtProvider(paths, NullLogger<AlbumArtProvider>.Instance),
            NullLogger<TestExportService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(new FakeLibraryClient(), new FfmpegInstall("x", "1", true),
            new ExportRequest(_dir.File("x"), LibrarySnapshot.Empty("1"), [], [], TagPolicy.Default), null, CancellationToken.None));
    }
}
