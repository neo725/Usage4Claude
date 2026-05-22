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

        var colorBitmap = CreateBitmap(IconSize, IconSize, 1, 32, pixels);
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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        uint[] bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateBitmap(
        int width,
        int height,
        uint planes,
        uint bitsPerPixel,
        byte[] bits);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconIndirect(ref IconInfo iconInfo);
}
