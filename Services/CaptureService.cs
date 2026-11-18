using System.Runtime.InteropServices;
using HDRSnip.Interop;
using HDRSnip.Models;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Format = Vortice.DXGI.Format;

namespace HDRSnip.Services;

public static class CaptureService
{
    private const int WaitTimeout = unchecked((int)0x887A0027);
    private const int SrgbLutSize = 16384;

    public static BitmapSource CaptureMonitor(MonitorInfo monitor)
    {
        try
        {
            var image = CaptureDxgi(monitor);
            Log.Info($"DXGI-захват: {monitor.Name} {image.PixelWidth}×{image.PixelHeight}");
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"DXGI недоступен для {monitor.Name}: {ex.Message}. Использую GDI.");
            try
            {
                var image = CaptureGdi(monitor);
                Log.Info($"GDI-захват: {monitor.Name} {image.PixelWidth}×{image.PixelHeight}");
                return image;
            }
            catch (Exception gdiEx)
            {
                Log.Error($"Не удалось захватить {monitor.Name}", gdiEx);
                throw;
            }
        }
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        int x = Math.Clamp(rect.X, 0, source.PixelWidth - 1);
        int y = Math.Clamp(rect.Y, 0, source.PixelHeight - 1);
        int w = Math.Clamp(rect.Width, 1, source.PixelWidth - x);
        int h = Math.Clamp(rect.Height, 1, source.PixelHeight - y);

        var cropped = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
        cropped.Freeze();
        return cropped;
    }

    private static bool Matches(IDXGIOutput output, MonitorInfo monitor)
    {
        try
        {
            var description = output.Description;
            if (!string.IsNullOrEmpty(monitor.DeviceName) &&
                string.Equals(description.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rect = description.DesktopCoordinates;
            return rect.Left == monitor.Bounds.Left && rect.Top == monitor.Bounds.Top &&
                   rect.Right == monitor.Bounds.Right && rect.Bottom == monitor.Bounds.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource CaptureDxgi(MonitorInfo monitor)
    {
        IDXGIAdapter1? matchedAdapter = null;
        IDXGIOutput? matchedOutput = null;

        using (var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure || adapter is null)
                    break;

                bool found = false;
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out var output).Failure || output is null)
                        break;

                    if (Matches(output, monitor))
                    {
                        matchedAdapter = adapter;
                        matchedOutput = output;
                        found = true;
                        break;
                    }

                    output.Dispose();
                }

                if (found)
                    break;

                adapter.Dispose();
            }
        }

        if (matchedAdapter is null || matchedOutput is null)
            throw new InvalidOperationException("Не найден DXGI-выход для монитора.");

        using (matchedAdapter)
        using (matchedOutput)
        {
            var colorSpace = ColorSpaceType.RgbFullG22NoneP709;
            try
            {
                using var output6 = matchedOutput.QueryInterface<IDXGIOutput6>();
                colorSpace = output6.Description1.ColorSpace;
            }
            catch
            {
                // Старые драйверы могут не поддерживать IDXGIOutput6.
            }

            bool pq = IsPq(colorSpace);
            bool scrgb = IsScRgb(colorSpace);
            Log.Info($"DXGI colorspace={colorSpace} pq={pq} scrgb={scrgb} white={monitor.SdrWhiteNits:0.#}");

            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var result = D3D11.D3D11CreateDevice(
                matchedAdapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                levels,
                out var device,
                out _,
                out var context);

            if (result.Failure || device is null || context is null)
                throw new InvalidOperationException("D3D11CreateDevice: " + result);

            using (device)
            using (context)
            {
                using var output1 = matchedOutput.QueryInterface<IDXGIOutput1>();
                using var duplication = CreateDuplication(output1, matchedOutput, device, pq || scrgb);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    if (attempt is 1 or 3 or 5)
                        NudgeCursor();

                    var acquired = duplication.AcquireNextFrame(attempt == 0 ? 600u : 900u, out _, out var resource);
                    if (acquired.Failure)
                    {
                        if (acquired.Code == WaitTimeout)
                            continue;

                        throw new InvalidOperationException("AcquireNextFrame: " + acquired);
                    }

                    try
                    {
                        using var texture = resource.QueryInterface<ID3D11Texture2D>();
                        var textureDescription = texture.Description;
                        int width = (int)textureDescription.Width;
                        int height = (int)textureDescription.Height;
                        if (width <= 0 || height <= 0)
                            throw new InvalidOperationException("Пустой кадр DXGI.");

                        var stagingDescription = new Texture2DDescription
                        {
                            Width = textureDescription.Width,
                            Height = textureDescription.Height,
                            MipLevels = 1,
                            ArraySize = 1,
                            Format = textureDescription.Format,
                            SampleDescription = new SampleDescription(1, 0),
                            Usage = ResourceUsage.Staging,
                            BindFlags = BindFlags.None,
                            CPUAccessFlags = CpuAccessFlags.Read,
                            MiscFlags = ResourceOptionFlags.None
                        };

                        using var staging = device.CreateTexture2D(stagingDescription);
                        context.CopyResource(staging, texture);
                        var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                        try
                        {
                            int bytesPerPixel = Math.Max(1, (int)(textureDescription.Format.GetBitsPerPixel() / 8));
                            if (IsBlankFrame(map, width, height, bytesPerPixel) && attempt < 4)
                            {
                                Log.Warn($"Пустой кадр DXGI (попытка {attempt + 1}, формат {textureDescription.Format}).");
                                continue;
                            }

                            var pixels = ConvertFrame(map, textureDescription.Format, colorSpace,
                                monitor.SdrWhiteNits, width, height);
                            return CreateBitmapSource(pixels, width, height);
                        }
                        finally
                        {
                            context.Unmap(staging, 0);
                        }
                    }
                    finally
                    {
                        duplication.ReleaseFrame();
                    }
                }
            }
        }

        throw new InvalidOperationException("DXGI не вернул валидный кадр.");
    }

    private static IDXGIOutputDuplication CreateDuplication(
        IDXGIOutput1 output1,
        IDXGIOutput output,
        ID3D11Device device,
        bool hdr)
    {
        if (hdr)
        {
            try
            {
                using var output5 = output.QueryInterface<IDXGIOutput5>();
                var duplication = output5.DuplicateOutput1(device, new[]
                {
                    Format.R16G16B16A16_Float,
                    Format.B8G8R8A8_UNorm
                });
                Log.Info("DXGI DuplicateOutput1 запрошен в R16G16B16A16_Float");
                return duplication;
            }
            catch (Exception ex)
            {
                Log.Warn("DuplicateOutput1 недоступен: " + ex.Message);
            }
        }

        return output1.DuplicateOutput(device);
    }

    private static void NudgeCursor()
    {
        try
        {
            if (!Native.GetCursorPos(out var position))
                return;

            Native.SetCursorPos(position.X + 1, position.Y);
            Thread.Sleep(45);
            Native.SetCursorPos(position.X, position.Y);
            Thread.Sleep(70);
        }
        catch
        {
            // Не критично.
        }
    }

    private static unsafe bool IsBlankFrame(MappedSubresource map, int width, int height, int bytesPerPixel)
    {
        if (map.DataPointer == IntPtr.Zero || width <= 1 || height <= 1)
            return true;

        int stepX = Math.Max(1, width / 48);
        int stepY = Math.Max(1, height / 48);
        int nonzero = 0;

        byte* basePointer = (byte*)map.DataPointer;
        for (int y = 0; y < height; y += stepY)
        {
            byte* row = basePointer + y * (int)map.RowPitch;
            for (int x = 0; x < width; x += stepX)
            {
                byte* pixel = row + x * bytesPerPixel;
                for (int b = 0; b < bytesPerPixel; b++)
                {
                    if (pixel[b] != 0)
                    {
                        nonzero++;
                        goto nextRow;
                    }
                }
            }

        nextRow: ;
        }

        return nonzero < 2;
    }

    private static unsafe byte[] ConvertFrame(
        MappedSubresource map,
        Format format,
        ColorSpaceType colorSpace,
        double sdrWhiteNits,
        int width,
        int height)
    {
        var output = new byte[width * height * 4];
        int rowPitch = (int)map.RowPitch;
        bool pq = IsPq(colorSpace);
        bool scrgb = IsScRgb(colorSpace);
        double white = Math.Max(80.0, sdrWhiteNits);

        if (format is Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb)
        {
            // Если драйвер всё же отдал 8-битный кадр для HDR-монитора, в нём
            // уже зашито масштабирование относительно SDR-белого (80 нит).
            // Возвращаем цвета к обычному SDR-виду, иначе картинка выглядит выбеленной.
            if ((pq || scrgb) && white > 80.5)
            {
                var lut = BuildHdrBgraLut(80.0 / white);
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)map.DataPointer + y * rowPitch;
                    int offset = y * width * 4;

                    for (int x = 0; x < width; x++)
                    {
                        byte* pixel = row + x * 4;
                        output[offset] = lut[pixel[0]];
                        output[offset + 1] = lut[pixel[1]];
                        output[offset + 2] = lut[pixel[2]];
                        output[offset + 3] = 255;
                        offset += 4;
                    }
                }

                return output;
            }

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(
                    IntPtr.Add(map.DataPointer, y * rowPitch),
                    output,
                    y * width * 4,
                    width * 4);
            }

            return output;
        }

        if (format == Format.R16G16B16A16_Float)
        {
            // Desktop Duplication отдаёт HDR-рабочий стол в линейном scRGB,
            // где 1.0 = 80 нит, независимо от PQ-кодировки вывода на монитор.
            float scale = (float)(80.0 / white);
            var halfToFloat = HalfToFloatLut.Value;

            Parallel.For(0, height, y =>
            {
                byte* row = (byte*)map.DataPointer + y * rowPitch;
                int offset = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    ushort* pixel = (ushort*)(row + x * 8);
                    float r = halfToFloat[pixel[0]] * scale;
                    float g = halfToFloat[pixel[1]] * scale;
                    float b = halfToFloat[pixel[2]] * scale;

                    output[offset] = LinearToSrgb(b);
                    output[offset + 1] = LinearToSrgb(g);
                    output[offset + 2] = LinearToSrgb(r);
                    output[offset + 3] = 255;
                    offset += 4;
                }
            });

            return output;
        }

        if (format == Format.R10G10B10A2_UNorm)
        {
            var pqLut = pq ? PqNitsLut.Value : null;

            Parallel.For(0, height, y =>
            {
                byte* row = (byte*)map.DataPointer + y * rowPitch;
                int offset = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    uint value = *(uint*)(row + x * 4);
                    float r10 = (value & 0x3FF) / 1023.0f;
                    float g10 = ((value >> 10) & 0x3FF) / 1023.0f;
                    float b10 = ((value >> 20) & 0x3FF) / 1023.0f;

                    byte r, g, b;
                    if (pq)
                    {
                        float rn = (float)(pqLut![Math.Clamp((int)(r10 * 1023f + 0.5f), 0, 1023)] / white);
                        float gn = (float)(pqLut[Math.Clamp((int)(g10 * 1023f + 0.5f), 0, 1023)] / white);
                        float bn = (float)(pqLut[Math.Clamp((int)(b10 * 1023f + 0.5f), 0, 1023)] / white);
                        (rn, gn, bn) = Rec2020To709(rn, gn, bn);
                        r = LinearToSrgb(rn);
                        g = LinearToSrgb(gn);
                        b = LinearToSrgb(bn);
                    }
                    else
                    {
                        r = (byte)Math.Clamp((int)(r10 * 255f + 0.5f), 0, 255);
                        g = (byte)Math.Clamp((int)(g10 * 255f + 0.5f), 0, 255);
                        b = (byte)Math.Clamp((int)(b10 * 255f + 0.5f), 0, 255);
                    }

                    output[offset] = b;
                    output[offset + 1] = g;
                    output[offset + 2] = r;
                    output[offset + 3] = 255;
                    offset += 4;
                }
            });

            return output;
        }

        throw new NotSupportedException("Неподдерживаемый формат кадра: " + format);
    }

    private static (float r, float g, float b) Rec2020To709(float r, float g, float b) =>
        (1.6605f * r - 0.5876f * g - 0.0728f * b,
         -0.1246f * r + 1.1329f * g - 0.0083f * b,
         -0.0182f * r - 0.1006f * g + 1.1187f * b);

    private static byte LinearToSrgb(float value)
    {
        var lut = SrgbLut.Value;
        if (float.IsNaN(value) || value <= 0)
            return lut[0];

        if (value >= 1f)
            return lut[^1];

        int index = (int)(value * (SrgbLutSize - 1) + 0.5f);
        return lut[index];
    }

    private static bool IsPq(ColorSpaceType colorSpace) =>
        colorSpace.ToString().Contains("G2084", StringComparison.OrdinalIgnoreCase);

    private static bool IsScRgb(ColorSpaceType colorSpace) =>
        colorSpace.ToString().Contains("G10", StringComparison.OrdinalIgnoreCase);

    private static byte[] BuildHdrBgraLut(double scale)
    {
        var lut = new byte[256];
        for (int i = 0; i < lut.Length; i++)
        {
            double value = i / 255.0;
            double linear = value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);
            lut[i] = LinearToSrgb((float)(linear * scale));
        }

        return lut;
    }

    private static readonly Lazy<float[]> HalfToFloatLut = new(() =>
    {
        var lut = new float[65536];
        for (int i = 0; i < lut.Length; i++)
            lut[i] = (float)BitConverter.Int16BitsToHalf((short)i);
        return lut;
    });

    private static readonly Lazy<float[]> PqNitsLut = new(() =>
    {
        var lut = new float[1024];
        for (int i = 0; i < lut.Length; i++)
            lut[i] = PqToNits(i / 1023.0);

        return lut;
    });

    private static readonly Lazy<byte[]> SrgbLut = new(() =>
    {
        var lut = new byte[SrgbLutSize];
        for (int i = 0; i < lut.Length; i++)
        {
            double linear = i / (double)(lut.Length - 1);
            double encoded = linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
            lut[i] = (byte)Math.Clamp((int)Math.Round(encoded * 255.0), 0, 255);
        }

        return lut;
    });

    private static float PqToNits(double value)
    {
        const double m1 = 2610.0 / 16384.0;
        const double m2 = 2523.0 / 4096.0 * 128.0;
        const double c1 = 3424.0 / 4096.0;
        const double c2 = 2413.0 / 4096.0 * 32.0;
        const double c3 = 2392.0 / 4096.0 * 32.0;

        double v = Math.Clamp(value, 0.0, 1.0);
        double p = Math.Pow(v, 1.0 / m2);
        double n = Math.Max(p - c1, 0.0) / (c2 - c3 * p);
        return (float)(Math.Pow(n, 1.0 / m1) * 10000.0);
    }

    private static BitmapSource CreateBitmapSource(byte[] pixels, int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static BitmapSource CaptureGdi(MonitorInfo monitor)
    {
        int width = monitor.PixelWidth;
        int height = monitor.PixelHeight;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Некорректный размер монитора.");

        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
            throw new InvalidOperationException("GetDC вернул NULL.");

        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            memoryDc = Native.CreateCompatibleDC(screenDc);
            bitmap = Native.CreateCompatibleBitmap(screenDc, width, height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                throw new InvalidOperationException("Не удалось создать GDI-буфер.");

            previous = Native.SelectObject(memoryDc, bitmap);
            if (!Native.BitBlt(memoryDc, 0, 0, width, height, screenDc, monitor.Bounds.Left, monitor.Bounds.Top,
                    Native.SrcCopy | Native.CaptureBlt))
            {
                throw new InvalidOperationException("BitBlt завершился ошибкой.");
            }

            var info = new Native.BitmapInfo
            {
                Header = new Native.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Native.BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = Native.DibRgbColors,
                    SizeImage = (uint)(width * height * 4)
                }
            };

            var pixels = new byte[width * height * 4];
            if (Native.GetDIBits(screenDc, bitmap, 0, (uint)height, pixels, ref info, Native.DibRgbColors) == 0)
                throw new InvalidOperationException("GetDIBits завершился ошибкой.");

            for (int offset = 3; offset < pixels.Length; offset += 4)
                pixels[offset] = 255;

            return CreateBitmapSource(pixels, width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
                Native.SelectObject(memoryDc, previous);

            if (bitmap != IntPtr.Zero)
                Native.DeleteObject(bitmap);

            if (memoryDc != IntPtr.Zero)
                Native.DeleteDC(memoryDc);

            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
