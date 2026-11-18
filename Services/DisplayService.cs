using System.Runtime.InteropServices;
using HDRSnip.Interop;
using HDRSnip.Models;

namespace HDRSnip.Services;

public static class DisplayService
{
    public static List<MonitorInfo> GetMonitors(bool writeLog = true)
    {
        var monitors = new List<MonitorInfo>();

        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hDc, ref Native.Rect rect, IntPtr data) =>
            {
                var info = new Native.MonitorInfoEx
                {
                    Size = Marshal.SizeOf<Native.MonitorInfoEx>(),
                    DeviceName = string.Empty
                };

                if (!Native.GetMonitorInfo(hMon, ref info))
                    return true;

                uint dpi = 96;
                try
                {
                    if (Native.GetDpiForMonitor(hMon, Native.MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                        dpi = dpiX;
                }
                catch
                {
                    // Оставляем 96 DPI.
                }

                monitors.Add(new MonitorInfo
                {
                    Handle = hMon,
                    DeviceName = info.DeviceName ?? string.Empty,
                    Bounds = info.Monitor,
                    Work = info.Work,
                    IsPrimary = (info.Flags & 1) != 0,
                    Dpi = dpi
                });

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перечислить мониторы", ex);
        }

        if (monitors.Count == 0)
        {
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            monitors.Add(new MonitorInfo
            {
                DeviceName = string.Empty,
                FriendlyName = "Экран",
                Bounds = new Native.Rect { Left = vs.Left, Top = vs.Top, Right = vs.Right, Bottom = vs.Bottom },
                Work = new Native.Rect { Left = vs.Left, Top = vs.Top, Right = vs.Right, Bottom = vs.Bottom },
                IsPrimary = true,
                Dpi = 96
            });
        }

        ApplyDisplayConfig(monitors);
        if (writeLog)
        {
            Log.Info("Мониторы: " + string.Join("; ", monitors.Select(m =>
                $"{m.DeviceName}/{m.Name} {m.ResolutionText} @{m.Scale:0.##}x {m.HdrText} work={m.Work.Left},{m.Work.Top},{m.Work.Right},{m.Work.Bottom}")));
        }

        return monitors
            .OrderByDescending(m => m.IsPrimary)
            .ThenBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    public static MonitorInfo? MonitorUnderCursor(IReadOnlyList<MonitorInfo> monitors)
    {
        if (!Native.GetCursorPos(out var point))
            return monitors.FirstOrDefault();

        return monitors.FirstOrDefault(m => m.Contains(point.X, point.Y)) ?? monitors.FirstOrDefault();
    }

    public static MonitorInfo? FindByDeviceName(IReadOnlyList<MonitorInfo> monitors, string deviceName) =>
        monitors.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    private static void ApplyDisplayConfig(List<MonitorInfo> monitors)
    {
        var entries = ReadDisplayConfig();
        if (entries.Count == 0)
            return;

        foreach (var monitor in monitors)
        {
            if (string.IsNullOrEmpty(monitor.DeviceName))
                continue;

            if (!entries.TryGetValue(monitor.DeviceName, out var entry))
                continue;

            monitor.IsHdr = entry.IsHdr;
            monitor.SdrWhiteNits = entry.SdrWhiteNits;
            if (!string.IsNullOrWhiteSpace(entry.FriendlyName))
                monitor.FriendlyName = entry.FriendlyName;
        }

        // Если DisplayConfig не сопоставился, но монитор один — разумно взять единственную запись.
        if (monitors.Count == 1 && entries.Count == 1)
        {
            var monitor = monitors[0];
            if (string.IsNullOrWhiteSpace(monitor.FriendlyName))
            {
                var entry = entries.Values.First();
                monitor.FriendlyName = entry.FriendlyName;
                monitor.IsHdr = entry.IsHdr;
                monitor.SdrWhiteNits = entry.SdrWhiteNits;
            }
        }
    }

    private sealed record DisplayConfigEntry(string FriendlyName, bool IsHdr, double SdrWhiteNits);

    private static Dictionary<string, DisplayConfigEntry> ReadDisplayConfig()
    {
        var result = new Dictionary<string, DisplayConfigEntry>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (Native.GetDisplayConfigBufferSizes(Native.QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0)
                return result;

            var paths = new Native.DisplayConfigPathInfo[pathCount];
            var modes = new Native.DisplayConfigModeInfo[modeCount];
            if (Native.QueryDisplayConfig(Native.QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
                return result;

            for (int i = 0; i < pathCount; i++)
            {
                var path = paths[i];

                var source = new Native.DisplayConfigSourceDeviceName
                {
                    Header = new Native.DisplayConfigDeviceInfoHeader
                    {
                        Type = Native.DisplayConfigDeviceInfoGetSourceName,
                        Size = (uint)Marshal.SizeOf<Native.DisplayConfigSourceDeviceName>(),
                        AdapterId = path.SourceInfo.AdapterId,
                        Id = path.SourceInfo.Id
                    }
                };

                if (Native.DisplayConfigGetDeviceInfo(ref source) != 0 ||
                    string.IsNullOrWhiteSpace(source.ViewGdiDeviceName))
                {
                    continue;
                }

                var target = new Native.DisplayConfigTargetDeviceName
                {
                    Header = new Native.DisplayConfigDeviceInfoHeader
                    {
                        Type = Native.DisplayConfigDeviceInfoGetTargetName,
                        Size = (uint)Marshal.SizeOf<Native.DisplayConfigTargetDeviceName>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };

                var friendly = "";
                if (Native.DisplayConfigGetDeviceInfo(ref target) == 0)
                    friendly = target.MonitorFriendlyDeviceName ?? "";

                var advanced = new Native.DisplayConfigAdvancedColorInfo
                {
                    Header = new Native.DisplayConfigDeviceInfoHeader
                    {
                        Type = Native.DisplayConfigDeviceInfoGetAdvancedColorInfo,
                        Size = (uint)Marshal.SizeOf<Native.DisplayConfigAdvancedColorInfo>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };

                bool hdr = false;
                if (Native.DisplayConfigGetDeviceInfo(ref advanced) == 0)
                    hdr = (advanced.Value & 0x2) != 0;

                var white = new Native.DisplayConfigSdrWhiteLevel
                {
                    Header = new Native.DisplayConfigDeviceInfoHeader
                    {
                        Type = Native.DisplayConfigDeviceInfoGetSdrWhiteLevel,
                        Size = (uint)Marshal.SizeOf<Native.DisplayConfigSdrWhiteLevel>(),
                        AdapterId = path.TargetInfo.AdapterId,
                        Id = path.TargetInfo.Id
                    }
                };

                double nits = 80;
                if (Native.DisplayConfigGetDeviceInfo(ref white) == 0 && white.SdrWhiteLevel > 0)
                    nits = white.SdrWhiteLevel / 1000.0 * 80.0;

                nits = Math.Clamp(nits, 50, 2000);
                result[source.ViewGdiDeviceName] = new DisplayConfigEntry(friendly, hdr, nits);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DisplayConfig недоступен: " + ex.Message);
        }

        return result;
    }
}
