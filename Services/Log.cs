namespace HDRSnip;

public static class Log
{
    private static readonly object Sync = new();

    public static string LogPath => Path.Combine(Models.SettingsService.DataFolder, "log.txt");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Models.SettingsService.DataFolder);
                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > 1_500_000)
                    file.Delete();

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (exception != null)
                    line += Environment.NewLine + exception;

                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Логирование не должно ломать приложение.
        }
    }
}
