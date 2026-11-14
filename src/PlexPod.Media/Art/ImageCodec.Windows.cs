#if WINDOWS
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PlexPod.Media.Art;

/// <summary>
/// Image decoding and baseline JPEG encoding with WIC. Plain pixel arrays only, no visuals, so it is
/// safe on background threads. The macOS build uses ImageIO instead (<c>ImageCodec.MacOS.cs</c>).
/// </summary>
internal static class ImageCodec
{
    /// <summary>Decodes the first frame to 24-bit BGR, rejecting images larger than <paramref name="maxSide"/> before allocating.</summary>
    public static (byte[] Bgr, int Width, int Height) DecodeBgr(byte[] source, int maxSide)
    {
        BitmapSource frame;
        using (var stream = new MemoryStream(source, writable: false))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }

        // Check the declared size before converting or allocating anything.
        CheckSize(frame.PixelWidth, frame.PixelHeight, maxSide);

        var bgr = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
        int w = bgr.PixelWidth, h = bgr.PixelHeight;
        var stride = w * 3;
        var raw = new byte[stride * h];
        bgr.CopyPixels(raw, stride, 0);
        return (raw, w, h);
    }

    /// <summary>24-bit BGR → baseline JPEG.</summary>
    public static byte[] EncodeJpeg(byte[] bgr, int width, int height, int quality)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bgr, width * 3);
        bitmap.Freeze();

        // WIC's JPEG encoder writes baseline (SOF0) JPEGs.
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>24-bit BGR → PNG. Used by tests to make lossless inputs.</summary>
    public static byte[] EncodePng(byte[] bgr, int width, int height)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bgr, width * 3)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    internal static void CheckSize(int width, int height, int maxSide)
    {
        if (width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Album art has no pixels.");
        }

        if (width > maxSide || height > maxSide)
        {
            throw new InvalidDataException($"Album art is {width}×{height}; the limit is {maxSide} px a side.");
        }
    }
}
#endif
