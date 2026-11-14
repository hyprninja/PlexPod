#if !WINDOWS
using System.Runtime.InteropServices;

namespace PlexPod.Media.Art;

/// <summary>
/// Image decoding and baseline JPEG encoding with Apple's ImageIO and Core Graphics, called directly
/// (no packages). ImageIO's JPEG encoder writes baseline (SOF0) JPEGs unless asked for progressive.
/// Thread-safe: every call creates and releases its own objects. The Windows build uses WIC instead.
/// </summary>
internal static partial class ImageCodec
{
    public static (byte[] Bgr, int Width, int Height) DecodeBgr(byte[] source, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(source);
        EnsureMacOS();
        var data = Cf.CreateData(source);
        var imageSource = IntPtr.Zero;
        var properties = IntPtr.Zero;
        var image = IntPtr.Zero;
        try
        {
            imageSource = Native.CGImageSourceCreateWithData(data, IntPtr.Zero);
            if (imageSource == IntPtr.Zero || Native.CGImageSourceGetCount(imageSource) < 1)
            {
                throw new InvalidDataException("Album art is not an image ImageIO can read.");
            }

            // Check the declared size before decoding or allocating anything.
            properties = Native.CGImageSourceCopyPropertiesAtIndex(imageSource, 0, IntPtr.Zero);
            CheckSize(Cf.GetInt(properties, Keys.PixelWidth), Cf.GetInt(properties, Keys.PixelHeight), maxSide);

            image = Native.CGImageSourceCreateImageAtIndex(imageSource, 0, IntPtr.Zero);
            if (image == IntPtr.Zero)
            {
                throw new InvalidDataException("Album art could not be decoded.");
            }

            int w = (int)Native.CGImageGetWidth(image), h = (int)Native.CGImageGetHeight(image);
            CheckSize(w, h, maxSide);
            var rgbx = new byte[w * h * 4];
            var handle = GCHandle.Alloc(rgbx, GCHandleType.Pinned);
            try
            {
                var context = CreateContext(handle.AddrOfPinnedObject(), w, h);
                try
                {
                    Native.CGContextDrawImage(context, new CGRect(0, 0, w, h), image);
                }
                finally
                {
                    Native.CGContextRelease(context);
                }
            }
            finally
            {
                handle.Free();
            }

            var bgr = new byte[w * h * 3];
            for (int i = 0, o = 0; i < rgbx.Length; i += 4, o += 3)
            {
                bgr[o] = rgbx[i + 2];
                bgr[o + 1] = rgbx[i + 1];
                bgr[o + 2] = rgbx[i];
            }

            return (bgr, w, h);
        }
        finally
        {
            Cf.Release(image);
            Cf.Release(properties);
            Cf.Release(imageSource);
            Cf.Release(data);
        }
    }

    public static byte[] EncodeJpeg(byte[] bgr, int width, int height, int quality) =>
        Encode(bgr, width, height, "public.jpeg", quality / 100.0);

    /// <summary>24-bit BGR → PNG. Used by tests to make lossless inputs.</summary>
    public static byte[] EncodePng(byte[] bgr, int width, int height) => Encode(bgr, width, height, "public.png", null);

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

    private static byte[] Encode(byte[] bgr, int width, int height, string type, double? quality)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        EnsureMacOS();
        var rgbx = new byte[width * height * 4];
        for (int i = 0, o = 0; i < bgr.Length; i += 3, o += 4)
        {
            rgbx[o] = bgr[i + 2];
            rgbx[o + 1] = bgr[i + 1];
            rgbx[o + 2] = bgr[i];
            rgbx[o + 3] = 0xFF;
        }

        var handle = GCHandle.Alloc(rgbx, GCHandleType.Pinned);
        IntPtr context = IntPtr.Zero, image = IntPtr.Zero, output = IntPtr.Zero, typeId = IntPtr.Zero;
        IntPtr destination = IntPtr.Zero, options = IntPtr.Zero;
        try
        {
            context = CreateContext(handle.AddrOfPinnedObject(), width, height);
            image = Native.CGBitmapContextCreateImage(context);
            output = Native.CFDataCreateMutable(IntPtr.Zero, 0);
            typeId = Cf.CreateString(type);
            destination = Native.CGImageDestinationCreateWithData(output, typeId, 1, IntPtr.Zero);
            if (image == IntPtr.Zero || destination == IntPtr.Zero)
            {
                throw new InvalidOperationException($"ImageIO could not create a {type} encoder.");
            }

            if (quality is { } q)
            {
                options = Cf.CreateDictionary(Keys.LossyCompressionQuality, Cf.CreateDouble(q));
            }

            Native.CGImageDestinationAddImage(destination, image, options);
            if (!Native.CGImageDestinationFinalize(destination))
            {
                throw new InvalidOperationException($"ImageIO could not encode the {type} image.");
            }

            var length = (int)Native.CFDataGetLength(output);
            var bytes = new byte[length];
            Marshal.Copy(Native.CFDataGetBytePtr(output), bytes, 0, length);
            return bytes;
        }
        finally
        {
            Cf.Release(options);
            Cf.Release(destination);
            Cf.Release(typeId);
            Cf.Release(output);
            Cf.Release(image);
            if (context != IntPtr.Zero)
            {
                Native.CGContextRelease(context);
            }

            handle.Free();
        }
    }

    /// <summary>An sRGB, 8 bits a channel, RGBX bitmap context over <paramref name="pixels"/>.</summary>
    private static IntPtr CreateContext(IntPtr pixels, int width, int height)
    {
        var space = Native.CGColorSpaceCreateWithName(Keys.SrgbColorSpace);
        try
        {
            var context = Native.CGBitmapContextCreate(pixels, (nuint)width, (nuint)height, 8, (nuint)(width * 4), space, Native.ImageAlphaNoneSkipLast);
            return context != IntPtr.Zero ? context : throw new InvalidOperationException("Core Graphics could not create a bitmap context.");
        }
        finally
        {
            Native.CGColorSpaceRelease(space);
        }
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Album art needs WIC (Windows) or ImageIO (macOS).");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    /// <summary>Framework constants (CFStringRef globals), read once.</summary>
    private static class Keys
    {
        public static readonly IntPtr PixelWidth = Global(Native.ImageIO, "kCGImagePropertyPixelWidth");
        public static readonly IntPtr PixelHeight = Global(Native.ImageIO, "kCGImagePropertyPixelHeight");
        public static readonly IntPtr LossyCompressionQuality = Global(Native.ImageIO, "kCGImageDestinationLossyCompressionQuality");
        public static readonly IntPtr SrgbColorSpace = Global(Native.CoreGraphics, "kCGColorSpaceSRGB");

        private static IntPtr Global(string library, string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(library), name));
    }

    /// <summary>Small Core Foundation helpers. Every Create/Copy result is released by the caller.</summary>
    private static class Cf
    {
        private const int NumberIntType = 9; // kCFNumberIntType
        private const int NumberDoubleType = 13; // kCFNumberDoubleType
        private const uint Utf8Encoding = 0x08000100; // kCFStringEncodingUTF8

        private static readonly IntPtr Library = NativeLibrary.Load(Native.CoreFoundation);
        private static readonly IntPtr KeyCallBacks = NativeLibrary.GetExport(Library, "kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr ValueCallBacks = NativeLibrary.GetExport(Library, "kCFTypeDictionaryValueCallBacks");

        public static unsafe IntPtr CreateData(byte[] bytes)
        {
            fixed (byte* p = bytes)
            {
                return Native.CFDataCreate(IntPtr.Zero, p, bytes.Length);
            }
        }

        public static IntPtr CreateString(string value) =>
            Native.CFStringCreateWithCString(IntPtr.Zero, value, Utf8Encoding);

        public static unsafe IntPtr CreateDouble(double value) =>
            Native.CFNumberCreate(IntPtr.Zero, NumberDoubleType, &value);

        /// <summary>A one-entry dictionary; takes ownership of <paramref name="value"/> (released here, retained by the dictionary).</summary>
        public static unsafe IntPtr CreateDictionary(IntPtr key, IntPtr value)
        {
            try
            {
                return Native.CFDictionaryCreate(IntPtr.Zero, &key, &value, 1, KeyCallBacks, ValueCallBacks);
            }
            finally
            {
                Release(value);
            }
        }

        public static unsafe int GetInt(IntPtr dictionary, IntPtr key)
        {
            if (dictionary == IntPtr.Zero)
            {
                return 0;
            }

            var number = Native.CFDictionaryGetValue(dictionary, key);
            var value = 0;
            return number != IntPtr.Zero && Native.CFNumberGetValue(number, NumberIntType, &value) ? value : 0;
        }

        public static void Release(IntPtr value)
        {
            if (value != IntPtr.Zero)
            {
                Native.CFRelease(value);
            }
        }
    }

    private static unsafe partial class Native
    {
        public const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        public const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        public const string ImageIO = "/System/Library/Frameworks/ImageIO.framework/ImageIO";

        public const uint ImageAlphaNoneSkipLast = 5; // kCGImageAlphaNoneSkipLast, default byte order: R G B X

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFDataCreate(IntPtr allocator, byte* bytes, nint length);

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFDataCreateMutable(IntPtr allocator, nint capacity);

        [LibraryImport(CoreFoundation)]
        public static partial nint CFDataGetLength(IntPtr data);

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFDataGetBytePtr(IntPtr data);

        [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFNumberCreate(IntPtr allocator, int type, void* value);

        [LibraryImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CFNumberGetValue(IntPtr number, int type, void* value);

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr* keys, IntPtr* values, nint count, IntPtr keyCallBacks, IntPtr valueCallBacks);

        [LibraryImport(CoreFoundation)]
        public static partial IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);

        [LibraryImport(CoreFoundation)]
        public static partial void CFRelease(IntPtr value);

        [LibraryImport(ImageIO)]
        public static partial IntPtr CGImageSourceCreateWithData(IntPtr data, IntPtr options);

        [LibraryImport(ImageIO)]
        public static partial nint CGImageSourceGetCount(IntPtr source);

        [LibraryImport(ImageIO)]
        public static partial IntPtr CGImageSourceCopyPropertiesAtIndex(IntPtr source, nint index, IntPtr options);

        [LibraryImport(ImageIO)]
        public static partial IntPtr CGImageSourceCreateImageAtIndex(IntPtr source, nint index, IntPtr options);

        [LibraryImport(ImageIO)]
        public static partial IntPtr CGImageDestinationCreateWithData(IntPtr data, IntPtr type, nint count, IntPtr options);

        [LibraryImport(ImageIO)]
        public static partial void CGImageDestinationAddImage(IntPtr destination, IntPtr image, IntPtr properties);

        [LibraryImport(ImageIO)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static partial bool CGImageDestinationFinalize(IntPtr destination);

        [LibraryImport(CoreGraphics)]
        public static partial nuint CGImageGetWidth(IntPtr image);

        [LibraryImport(CoreGraphics)]
        public static partial nuint CGImageGetHeight(IntPtr image);

        [LibraryImport(CoreGraphics)]
        public static partial IntPtr CGColorSpaceCreateWithName(IntPtr name);

        [LibraryImport(CoreGraphics)]
        public static partial void CGColorSpaceRelease(IntPtr space);

        [LibraryImport(CoreGraphics)]
        public static partial IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr space, uint bitmapInfo);

        [LibraryImport(CoreGraphics)]
        public static partial IntPtr CGBitmapContextCreateImage(IntPtr context);

        [LibraryImport(CoreGraphics)]
        public static partial void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);

        [LibraryImport(CoreGraphics)]
        public static partial void CGContextRelease(IntPtr context);
    }
}
#endif
