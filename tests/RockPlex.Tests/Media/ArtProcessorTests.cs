using System.Windows.Media;
using System.Windows.Media.Imaging;
using RockPlex.Media.Art;

namespace RockPlex.Tests.Media;

public sealed class ArtProcessorTests
{
    private static byte[] Png(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2]) = (b, g, r);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, pixels, w * 3)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        return BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
    }

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
        var bitmap = new FormatConvertedBitmap(Decode(art.EmbeddedJpeg), PixelFormats.Bgr24, null, 0);
        Assert.Equal(300, bitmap.PixelWidth);
        Assert.Equal(300, bitmap.PixelHeight);

        var px = new byte[3];
        bitmap.CopyPixels(new System.Windows.Int32Rect(150, 150, 1, 1), px, 3, 0);
        Assert.True(px[0] > 200 && px[2] < 50, "centre should be blue");
        bitmap.CopyPixels(new System.Windows.Int32Rect(150, 5, 1, 1), px, 3, 0);
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
}
