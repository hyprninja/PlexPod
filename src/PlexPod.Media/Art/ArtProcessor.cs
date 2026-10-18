using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlexPod.Media.Art;

/// <summary>Album art ready for the iPod: a small embedded cover and a larger <c>cover.jpg</c>.</summary>
public sealed record ArtImages(byte[] EmbeddedJpeg, byte[] CoverJpeg);

/// <summary>
/// Plex poster → baseline JPEGs (Rockbox can't show progressive JPEGs). Non-square art is padded to
/// square, and images are only ever scaled down. Uses WIC codecs on plain pixel arrays, with no
/// visuals, so it is safe on background threads.
/// </summary>
public static class ArtProcessor
{
    public const int EmbeddedSize = 300;
    public const int CoverSize = 500;
    public const int JpegQuality = 85;

    /// <summary>Largest source side accepted; posters are never this big, and it bounds memory (side² × 3 bytes).</summary>
    public const int MaxSourceSide = 8000;

    public static ArtImages Process(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var (pixels, side) = DecodeSquare(source);
        return new ArtImages(
            Encode(Downscale(pixels, side, Math.Min(side, EmbeddedSize)), Math.Min(side, EmbeddedSize)),
            Encode(Downscale(pixels, side, Math.Min(side, CoverSize)), Math.Min(side, CoverSize)));
    }

    /// <summary>Decodes to 24-bit BGR, centred on a black square canvas.</summary>
    private static (byte[] Pixels, int Side) DecodeSquare(byte[] source)
    {
        BitmapSource frame;
        using (var stream = new MemoryStream(source, writable: false))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        // Check the declared size before converting or allocating anything.
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0)
        {
            throw new InvalidDataException("Album art has no pixels.");
        }

        if (frame.PixelWidth > MaxSourceSide || frame.PixelHeight > MaxSourceSide)
        {
            throw new InvalidDataException($"Album art is {frame.PixelWidth}×{frame.PixelHeight}; the limit is {MaxSourceSide} px a side.");
        }

        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int w = bgr.PixelWidth, h = bgr.PixelHeight;

        var stride = w * 3;
        var raw = new byte[stride * h];
        bgr.CopyPixels(raw, stride, 0);

        var side = Math.Max(w, h);
        var square = new byte[side * side * 3];
        int offX = (side - w) / 2, offY = (side - h) / 2;
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(raw, y * stride, square, ((y + offY) * side + offX) * 3, stride);
        }

        return (square, side);
    }

    /// <summary>Area-averaging downscale (each output pixel is the mean of the source area it covers).</summary>
    private static byte[] Downscale(byte[] src, int srcSide, int dstSide)
    {
        if (dstSide == srcSide)
        {
            return src;
        }

        var dst = new byte[dstSide * dstSide * 3];
        var scale = (double)srcSide / dstSide;
        var sums = new double[3];
        for (var dy = 0; dy < dstSide; dy++)
        {
            double y0 = dy * scale, y1 = y0 + scale;
            for (var dx = 0; dx < dstSide; dx++)
            {
                double x0 = dx * scale, x1 = x0 + scale;
                Array.Clear(sums);
                double area = 0;
                for (var sy = (int)y0; sy < Math.Min(srcSide, (int)Math.Ceiling(y1)); sy++)
                {
                    var wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);
                    for (var sx = (int)x0; sx < Math.Min(srcSide, (int)Math.Ceiling(x1)); sx++)
                    {
                        var weight = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));
                        var i = (sy * srcSide + sx) * 3;
                        sums[0] += src[i] * weight;
                        sums[1] += src[i + 1] * weight;
                        sums[2] += src[i + 2] * weight;
                        area += weight;
                    }
                }

                var o = (dy * dstSide + dx) * 3;
                for (var c = 0; c < 3; c++)
                {
                    dst[o + c] = (byte)Math.Clamp(Math.Round(sums[c] / area), 0, 255);
                }
            }
        }

        return dst;
    }

    private static byte[] Encode(byte[] pixels, int side)
    {
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgr24, null, pixels, side * 3);
        bitmap.Freeze();

        // WIC's JPEG encoder writes baseline (SOF0) JPEGs.
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>True when the JPEG has a baseline SOF0 marker and no progressive SOF2 marker.</summary>
    public static bool IsBaselineJpeg(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return false;
        }

        var i = 2;
        while (i + 4 <= jpeg.Length)
        {
            if (jpeg[i] != 0xFF)
            {
                return false;
            }

            var marker = jpeg[i + 1];
            switch (marker)
            {
                case 0xC0:
                    return true;
                case 0xC2:
                    return false;
                case 0xD9 or 0xDA:
                    return false;
            }

            var length = jpeg[i + 2] << 8 | jpeg[i + 3];
            i += 2 + length;
        }

        return false;
    }
}
