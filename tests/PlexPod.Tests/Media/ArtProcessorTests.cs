using PlexPod.Media.Art;

namespace PlexPod.Tests.Media;

public sealed class ArtProcessorTests
{
    private static byte[] Png(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2]) = (b, g, r);
        }

        return ImageCodec.EncodePng(pixels, w, h);
    }

    private static (byte[] Bgr, int PixelWidth, int PixelHeight) Decode(byte[] jpeg) => ImageCodec.DecodeBgr(jpeg, int.MaxValue);

    private static byte[] PixelAt((byte[] Bgr, int PixelWidth, int PixelHeight) bitmap, int x, int y) =>
        bitmap.Bgr.AsSpan(((y * bitmap.PixelWidth) + x) * 3, 3).ToArray();

    [Fact]
    public void LargeArt_BecomesBaseline300And500()
    {
        var art = ArtProcessor.Process(Png(1400, 1400, 200, 30, 30));
        Assert.True(ArtProcessor.IsBaselineJpeg(art.EmbeddedJpeg));
        Assert.True(ArtProcessor.IsBaselineJpeg(art.CoverJpeg));
        Assert.Equal((300, 300), (Decode(art.EmbeddedJpeg).PixelWidth, Decode(art.EmbeddedJpeg).PixelHeight));
        Assert.Equal((500, 500), (Decode(art.CoverJpeg).PixelWidth, Decode(art.CoverJpeg).PixelHeight));
        Assert.InRange(art.EmbeddedJpeg.Length, 100, 60_000);
    }

    [Fact]
    public void NonSquare_IsPaddedToSquare_ColourKeptInTheMiddle()
    {
        var art = ArtProcessor.Process(Png(1000, 500, 0, 0, 255));
        var bitmap = Decode(art.EmbeddedJpeg);
        Assert.Equal(300, bitmap.PixelWidth);
        Assert.Equal(300, bitmap.PixelHeight);

        var px = PixelAt(bitmap, 150, 150);
        Assert.True(px[0] > 200 && px[2] < 50, "centre should be blue");
        px = PixelAt(bitmap, 150, 5);
        Assert.True(px[0] < 40 && px[1] < 40 && px[2] < 40, "top band should be black padding");
    }

    [Fact]
    public void SmallArt_IsNeverScaledUp()
    {
        var art = ArtProcessor.Process(Png(120, 120, 10, 200, 10));
        Assert.Equal(120, Decode(art.EmbeddedJpeg).PixelWidth);
        Assert.Equal(120, Decode(art.CoverJpeg).PixelWidth);
    }

    [Fact]
    public void Garbage_Throws() =>
        Assert.ThrowsAny<Exception>(() => ArtProcessor.Process([1, 2, 3, 4]));

    [Fact]
    public void BaselineCheck_RejectsProgressiveMarker()
    {
        byte[] progressive = [0xFF, 0xD8, 0xFF, 0xC2, 0x00, 0x02];
        byte[] baseline = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xC0, 0x00, 0x02];
        Assert.False(ArtProcessor.IsBaselineJpeg(progressive));
        Assert.True(ArtProcessor.IsBaselineJpeg(baseline));
        Assert.False(ArtProcessor.IsBaselineJpeg([0x89, 0x50]));
    }

    [Fact]
    public void Rgb565_PacksLittleEndian_AndScalesToFit()
    {
        var gray = TagWriterTests.Jpeg(600, 600); // mid grey 0x80
        var (pixels, width, height, padX, padY) = ArtProcessor.ToRgb565(gray, 100, 100);
        Assert.Equal(100 * 100 * 2, pixels.Length);
        Assert.Equal((100, 100, 0, 0), (width, height, padX, padY));
        var first = (ushort)(pixels[0] | (pixels[1] << 8));
        Assert.InRange(first >> 11, 15, 16);          // red: 128 >> 3
        Assert.InRange((first >> 5) & 0x3F, 31, 32);  // green: 128 >> 2
        Assert.InRange(first & 0x1F, 15, 16);         // blue
    }

    [Fact]
    public void Rgb565_SmallCover_IsCentredNotUpscaled()
    {
        var small = TagWriterTests.Jpeg(64, 64);
        var (pixels, width, height, padX, padY) = ArtProcessor.ToRgb565(small, 100, 100);
        Assert.Equal((82, 82, 18, 18), (width, height, padX, padY)); // inclusive of padding, as libgpod records it
        Assert.Equal(0, pixels[0] | pixels[1]);                      // black border
        var centre = ((50 * 100) + 50) * 2;
        Assert.NotEqual(0, pixels[centre] | pixels[centre + 1]);
    }
}
