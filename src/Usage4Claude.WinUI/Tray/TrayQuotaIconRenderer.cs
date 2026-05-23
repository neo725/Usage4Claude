using System.Runtime.InteropServices;

namespace Usage4Claude.WinUI.Tray;

internal static class TrayQuotaIconRenderer
{
    private const int IconSize = 32;

    public static nint CreateIcon(double percentage)
    {
        var pixels = new uint[IconSize * IconSize];
        var clampedPercentage = Math.Clamp(percentage, 0, 100);
        var progressAngle = clampedPercentage / 100 * Math.Tau;
        var progressColor = GetProgressColor(clampedPercentage);
        var center = (IconSize - 1) / 2d;

        for (var y = 0; y < IconSize; y++)
        {
            for (var x = 0; x < IconSize; x++)
            {
                var dx = x - center;
                var dy = y - center;
                var radius = Math.Sqrt(dx * dx + dy * dy);
                var pixelIndex = y * IconSize + x;

                if (radius is >= 10.75 and <= 14.5)
                {
                    var angle = Math.Atan2(dx, -dy);
                    if (angle < 0)
                    {
                        angle += Math.Tau;
                    }

                    pixels[pixelIndex] = angle <= progressAngle
                        ? progressColor
                        : ToBgra(122, 122, 122, 172);
                    continue;
                }

                if (radius <= 8.5)
                {
                    pixels[pixelIndex] = radius <= 7.5
                        ? ToBgra(204, 111, 74, 255)
                        : ToBgra(95, 46, 33, 255);
                }
            }
        }

        var colorBitmap = CreateColorBitmap(pixels);
        var maskBitmap = CreateBitmap(IconSize, IconSize, 1, 1, new byte[IconSize * IconSize / 8]);
        if (colorBitmap == nint.Zero || maskBitmap == nint.Zero)
        {
            DeleteObject(colorBitmap);
            DeleteObject(maskBitmap);
            return nint.Zero;
        }

        try
        {
            var info = new IconInfo
            {
                IsIcon = true,
                ColorBitmap = colorBitmap,
                MaskBitmap = maskBitmap,
            };

            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(colorBitmap);
            DeleteObject(maskBitmap);
        }
    }

    private static uint GetProgressColor(double percentage) =>
        percentage switch
        {
            >= 90 => ToBgra(210, 72, 72, 255),
            >= 70 => ToBgra(224, 161, 60, 255),
            _ => ToBgra(62, 176, 112, 255),
        };

    private static uint ToBgra(byte red, byte green, byte blue, byte alpha) =>
        (uint)(alpha << 24 | red << 16 | green << 8 | blue);

    private static nint CreateColorBitmap(uint[] pixels)
    {
        var bitmapInfo = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = IconSize,
                Height = -IconSize,
                Planes = 1,
                BitsPerPixel = 32,
                Compression = BitmapCompression.Rgb,
                ImageSize = IconSize * IconSize * 4,
            },
        };

        var bitmap = CreateDIBSection(
            nint.Zero,
            ref bitmapInfo,
            DibColorMode.RgbColors,
            out var bitmapBits,
            nint.Zero,
            0);
        if (bitmap == nint.Zero || bitmapBits == nint.Zero)
        {
            return nint.Zero;
        }

        Marshal.Copy(
            pixels.Select(unchecked(value => (int)value)).ToArray(),
            0,
            bitmapBits,
            pixels.Length);
        return bitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitsPerPixel;
        public BitmapCompression Compression;
        public int ImageSize;
        public int XpixelsPerMeter;
        public int YpixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool IsIcon;

        public uint HotspotX;
        public uint HotspotY;
        public nint MaskBitmap;
        public nint ColorBitmap;
    }

    private enum BitmapCompression : uint
    {
        Rgb = 0,
    }

    private enum DibColorMode : uint
    {
        RgbColors = 0,
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        byte[] bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateDIBSection(
        nint deviceContext,
        ref BitmapInfo bitmapInfo,
        DibColorMode usage,
        out nint bits,
        nint section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref IconInfo iconInfo);
}
