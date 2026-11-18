using System.Diagnostics;
using System.Windows.Media.Imaging;

namespace HDRSnip.Services;

public static class OutputService
{
    public static string BuildFileName(DateTime moment, int index) =>
        $"Снимок экрана {moment:yyyy-MM-dd HH-mm-ss} {index}";

    public static string SavePng(BitmapSource image, string folder)
    {
        Directory.CreateDirectory(folder);

        var moment = DateTime.Now;
        var date = moment.ToString("yyyy-MM-dd");
        int index = GetNextIndex(folder, date);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        while (true)
        {
            var path = Path.Combine(folder, BuildFileName(moment, index) + ".png");

            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                encoder.Save(stream);
                Log.Info("Сохранено: " + path);
                return path;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Файл успели создать между проверкой и записью — берём следующий номер.
                index++;
            }
        }
    }

    private static int GetNextIndex(string folder, string date)
    {
        var prefix = $"Снимок экрана {date} ";
        int max = 0;

        foreach (var file in Directory.EnumerateFiles(folder, "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // Новый формат: дата, время и номер за день через пробел.
            // Старые файлы без номера или с другим временем просто пропускаем.
            var parts = name[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 8)
                continue;

            if (int.TryParse(parts[1], out int index) && index > max)
                max = index;
        }

        return max + 1;
    }

    public static void CopyToClipboard(BitmapSource image)
    {
        Exception? last = null;

        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var pngStream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(pngStream);
                pngStream.Position = 0;

                using var bitmap = ToGdiBitmap(image);

                var data = new System.Windows.Forms.DataObject();
                data.SetData("PNG", false, pngStream);
                data.SetData(System.Windows.Forms.DataFormats.Bitmap, true, bitmap);
                System.Windows.Forms.Clipboard.SetDataObject(data, true);
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(70);
            }
        }

        throw new InvalidOperationException("Не удалось скопировать изображение в буфер обмена.", last);
    }

    public static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{folder}\"",
            UseShellExecute = true
        });
    }

    private static System.Drawing.Bitmap ToGdiBitmap(BitmapSource source)
    {
        BitmapSource bgra = source;
        if (bgra.Format != PixelFormats.Bgra32 && bgra.Format != PixelFormats.Pbgra32)
        {
            bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
        }

        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            bgra.CopyPixels(new Int32Rect(0, 0, width, height), data.Scan0, data.Stride * height, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
