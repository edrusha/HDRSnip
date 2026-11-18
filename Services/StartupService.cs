using Microsoft.Win32;

namespace HDRSnip.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HDRSnip";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) ||
                string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warn("Автозапуск не настроен: приложение запущено не через HDRSnip.exe.");
                return;
            }

            key.SetValue(ValueName, $"\"{exe}\" --tray");
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось изменить автозапуск: " + ex.Message);
        }
    }
}
