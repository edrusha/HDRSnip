using System.Text.Json;

namespace HDRSnip.Models;

public sealed class AppSettings
{
    public bool SaveToFile { get; set; } = false;
    public string SaveFolder { get; set; } = DefaultSaveFolder;
    public bool ShowAnnotationTools { get; set; } = true;
    public string AnnotationTool { get; set; } = "Brush";
    public string AnnotationColor { get; set; } = "#FF4D4F";
    public double AnnotationThickness { get; set; } = 6.5;
    public bool ShowCursor { get; set; } = true;
    public bool CaptureFullMonitor { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool StartMinimized { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public uint HotkeyModifiers { get; set; } = 0;
    public uint HotkeyKey { get; set; } = 0x2C; // VK_SNAPSHOT
    public uint FullMonitorHotkeyModifiers { get; set; } = HotkeyBinding.ModControl;
    public uint FullMonitorHotkeyKey { get; set; } = 0x2C; // Ctrl + VK_SNAPSHOT

    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Скриншоты");

    public HotkeyBinding Hotkey => new(HotkeyModifiers, HotkeyKey);
    public HotkeyBinding FullMonitorHotkey => new(FullMonitorHotkeyModifiers, FullMonitorHotkeyKey);

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(SaveFolder))
            SaveFolder = DefaultSaveFolder;

        if (AnnotationThickness < 1 || AnnotationThickness > 24)
            AnnotationThickness = 6.5;

        if (AnnotationTool is not ("Brush" or "Arrow" or "Line" or "Rectangle" or
            "Ellipse" or "Highlighter" or "Text" or "Blur"))
        {
            AnnotationTool = "Brush";
        }

        if (string.IsNullOrWhiteSpace(AnnotationColor))
            AnnotationColor = "#FF4D4F";

        if (HotkeyKey == 0)
        {
            HotkeyModifiers = 0;
            HotkeyKey = 0x2C;
        }

        if (FullMonitorHotkeyKey == 0)
        {
            FullMonitorHotkeyModifiers = HotkeyBinding.ModControl;
            FullMonitorHotkeyKey = 0x2C;
        }

        if (FullMonitorHotkeyKey == HotkeyKey &&
            FullMonitorHotkeyModifiers == HotkeyModifiers)
        {
            FullMonitorHotkeyModifiers = HotkeyBinding.ModControl | HotkeyBinding.ModShift;
            FullMonitorHotkeyKey = 0x2C;
        }
    }
}

public static class SettingsService
{
    private static readonly object Sync = new();

    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDRSnip");

    public static string SettingsPath => Path.Combine(DataFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось прочитать настройки", ex);
            try
            {
                var bad = SettingsPath + ".bad";
                if (File.Exists(SettingsPath) && !File.Exists(bad))
                    File.Move(SettingsPath, bad);
            }
            catch
            {
                // ignore
            }

            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DataFolder);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, SettingsPath, true);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
