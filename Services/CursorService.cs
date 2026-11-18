using System.Runtime.InteropServices;
using HDRSnip.Interop;

namespace HDRSnip.Services;

/// <summary>
/// Снимок системного курсора: значок с прозрачностью, горячая точка и
/// экранная позиция на момент захвата.
/// </summary>
public sealed class CursorSnapshot
{
    internal CursorSnapshot(BitmapSource image, int hotspotX, int hotspotY, int screenX, int screenY)
    {
        Image = image;
        HotspotX = hotspotX;
        HotspotY = hotspotY;
        ScreenX = screenX;
        ScreenY = screenY;
    }

    public BitmapSource Image { get; }
    public int Width => Image.PixelWidth;
    public int Height => Image.PixelHeight;
    public int HotspotX { get; }
    public int HotspotY { get; }
    public int ScreenX { get; }
    public int ScreenY { get; }
}

public static class CursorService
{
    public static CursorSnapshot? Capture()
    {
        try
        {
            var info = new Native.CursorInfo { Size = Marshal.SizeOf<Native.CursorInfo>() };
            if (!Native.GetCursorInfo(ref info))
                return null;

            if ((info.Flags & Native.CursorShowing) == 0 || info.Cursor == IntPtr.Zero)
                return null;

            var icon = Native.CopyIcon(info.Cursor);
            if (icon == IntPtr.Zero)
                return null;

            var colorBitmap = IntPtr.Zero;
            var maskBitmap = IntPtr.Zero;

            try
            {
                if (!Native.GetIconInfo(icon, out var iconInfo))
                    return null;

                colorBitmap = iconInfo.ColorBitmap;
                maskBitmap = iconInfo.MaskBitmap;

                var (width, height) = GetSize(iconInfo);
                var image = ReadIcon(width, height, colorBitmap, maskBitmap,
                    monochrome: colorBitmap == IntPtr.Zero);

                return new CursorSnapshot(image, iconInfo.HotspotX, iconInfo.HotspotY,
                    info.ScreenPosition.X, info.ScreenPosition.Y);
            }
            finally
            {
                if (colorBitmap != IntPtr.Zero)
                    Native.DeleteObject(colorBitmap);
                if (maskBitmap != IntPtr.Zero)
                    Native.DeleteObject(maskBitmap);
                Native.DestroyIcon(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось снять курсор: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Рисует курсор на готовом изображении. Координаты — пиксели изображения.
    /// Смешивание выполняется вручную, поэтому корректно работают и цветные
    /// курсоры с альфа-каналом, и старые монохромные.
    /// </summary>
    public static BitmapSource Draw(BitmapSource image, CursorSnapshot cursor, int x, int y)
    {
        var source = ToBgra32(image);
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;

        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        int iconStride = cursor.Width * 4;
        var icon = new byte[iconStride * cursor.Height];
        cursor.Image.CopyPixels(icon, iconStride, 0);

        // Держим курсор целиком внутри снимка, чтобы у края он не обрезался.
        int drawX = Math.Clamp(x - cursor.HotspotX, 0, Math.Max(0, width - cursor.Width));
        int drawY = Math.Clamp(y - cursor.HotspotY, 0, Math.Max(0, height - cursor.Height));

        for (int row = 0; row < cursor.Height; row++)
        {
            int dy = drawY + row;
            if (dy < 0 || dy >= height)
                continue;

            for (int col = 0; col < cursor.Width; col++)
            {
                int dx = drawX + col;
                if (dx < 0 || dx >= width)
                    continue;

                int si = row * iconStride + col * 4;
                int alpha = icon[si + 3];
                if (alpha == 0)
                    continue;

                int di = dy * stride + dx * 4;
                if (alpha == 255)
                {
                    pixels[di] = icon[si];
                    pixels[di + 1] = icon[si + 1];
                    pixels[di + 2] = icon[si + 2];
                }
                else
                {
                    int inverse = 255 - alpha;
                    pixels[di] = (byte)((icon[si] * alpha + pixels[di] * inverse + 127) / 255);
                    pixels[di + 1] = (byte)((icon[si + 1] * alpha + pixels[di + 1] * inverse + 127) / 255);
                    pixels[di + 2] = (byte)((icon[si + 2] * alpha + pixels[di + 2] * inverse + 127) / 255);
                }

                pixels[di + 3] = 255;
            }
        }

        var result = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static BitmapSource ReadIcon(int width, int height, IntPtr colorBitmap,
        IntPtr maskBitmap, bool monochrome)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC вернул NULL.");

        try
        {
            if (monochrome)
            {
                // Маска монохромного курсора состоит из двух половин:
                // верхняя — AND (прозрачность), нижняя — XOR (цвет).
                var mask = ReadAsBgra(screenDc, maskBitmap, width, height * 2);
                var pixels = new byte[width * height * 4];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int mi = (y * width + x) * 4;
                        int xi = ((y + height) * width + x) * 4;
                        bool transparent = mask[mi] != 0;
                        int di = (y * width + x) * 4;

                        if (transparent)
                            continue;

                        byte value = mask[xi] != 0 ? (byte)255 : (byte)0;
                        pixels[di] = value;
                        pixels[di + 1] = value;
                        pixels[di + 2] = value;
                        pixels[di + 3] = 255;
                    }
                }

                return CreateBitmap(width, height, pixels);
            }

            var color = ReadAsBgra(screenDc, colorBitmap, width, height);

            bool hasAlpha = false;
            for (int i = 3; i < color.Length; i += 4)
            {
                if (color[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (maskBitmap != IntPtr.Zero)
            {
                var mask = ReadAsBgra(screenDc, maskBitmap, width, height);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int mi = (y * width + x) * 4;
                        int ci = (y * width + x) * 4;

                        if (mask[mi] != 0)
                            color[ci + 3] = 0;
                        else if (!hasAlpha)
                            color[ci + 3] = 255;
                    }
                }
            }
            else if (!hasAlpha)
            {
                for (int i = 0; i < color.Length; i += 4)
                    color[i + 3] = 255;
            }

            return CreateBitmap(width, height, color);
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static byte[] ReadAsBgra(IntPtr screenDc, IntPtr bitmap, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var info = new Native.BitmapInfo
        {
            Header = new Native.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)pixels.Length
            }
        };

        int lines = Native.GetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref info,
            Native.DibRgbColors);
        if (lines == 0)
            throw new InvalidOperationException("GetDIBits вернул 0 строк.");

        return pixels;
    }

    private static BitmapSource CreateBitmap(int width, int height, byte[] pixels)
    {
        var image = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32)
            return source;

        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        converted.Freeze();
        return converted;
    }

    private static (int Width, int Height) GetSize(Native.IconInfo info)
    {
        var bitmap = info.ColorBitmap != IntPtr.Zero ? info.ColorBitmap : info.MaskBitmap;
        int width = 0;
        int height = 0;

        if (bitmap != IntPtr.Zero)
        {
            var data = default(Native.Bitmap);
            if (Native.GetObject(bitmap, Marshal.SizeOf<Native.Bitmap>(), ref data) != 0)
            {
                width = data.Width;
                height = data.Height;
            }
        }

        // У монохромного курсора маска содержит две половины: AND и XOR.
        if (info.ColorBitmap == IntPtr.Zero)
            height /= 2;

        if (width <= 0 || height <= 0)
        {
            width = Math.Max(1, Native.GetSystemMetrics(Native.SmCxCursor));
            height = Math.Max(1, Native.GetSystemMetrics(Native.SmCyCursor));
        }

        return (width, height);
    }
}
