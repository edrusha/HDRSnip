using System.Runtime.InteropServices;
using System.Text;
using HDRSnip.Interop;

namespace HDRSnip.Services;

public sealed record WindowHit(IntPtr Handle, string Title, Native.Rect Bounds);

public static class WindowCaptureService
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    /// <summary>Верхнее видимое окно под указанной точкой экрана, кроме окон HDR Snip.</summary>
    public static WindowHit? FindWindowAt(int screenX, int screenY)
    {
        WindowHit? hit = null;
        uint currentProcess = (uint)Environment.ProcessId;

        try
        {
            // EnumWindows обходит окна сверху вниз, поэтому первое подходящее
            // окно под точкой — то, которое пользователь реально видит.
            Native.EnumWindows((hwnd, _) =>
            {
                if (hit is not null)
                    return false;

                if (!IsCandidate(hwnd, currentProcess))
                    return true;

                if (!TryGetBounds(hwnd, out var bounds))
                    return true;

                if (bounds.Width < 8 || bounds.Height < 8)
                    return true;

                if (screenX < bounds.Left || screenX >= bounds.Right ||
                    screenY < bounds.Top || screenY >= bounds.Bottom)
                {
                    return true;
                }

                hit = new WindowHit(hwnd, GetTitle(hwnd), bounds);
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось определить окно под курсором: " + ex.Message);
        }

        return hit;
    }

    public static bool TryGetBounds(IntPtr hwnd, out Native.Rect bounds)
    {
        // Границы DWM не включают невидимую рамку изменения размера
        // и тень, поэтому снимок получается ровно по видимому окну.
        try
        {
            if (Native.DwmGetWindowAttribute(hwnd, Native.DwmwaExtendedFrameBounds,
                    out bounds, Marshal.SizeOf<Native.Rect>()) == 0 &&
                bounds.Width > 0 && bounds.Height > 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("DWM не отдал границы окна: " + ex.Message);
        }

        return Native.GetWindowRect(hwnd, out bounds) &&
               bounds.Width > 0 && bounds.Height > 0;
    }

    private static bool IsCandidate(IntPtr hwnd, uint currentProcess)
    {
        if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd))
            return false;

        int exStyle = Native.GetWindowLong(hwnd, Native.GwlExStyle);
        if ((exStyle & Native.WsExTransparent) != 0)
            return false;

        Native.GetWindowThreadProcessId(hwnd, out uint processId);
        if (processId == currentProcess)
            return false;

        try
        {
            // Окна UWP с других виртуальных рабочих столов существуют,
            // но не видны — их нужно пропустить.
            if (Native.DwmGetWindowAttribute(hwnd, Native.DwmwaCloaked,
                    out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return false;
            }
        }
        catch
        {
            // На старых системах атрибута может не быть — это не ошибка.
        }

        return !IgnoredClasses.Contains(GetClassName(hwnd));
    }

    private static string GetTitle(IntPtr hwnd)
    {
        try
        {
            int length = Native.GetWindowTextLength(hwnd);
            if (length <= 0)
                return string.Empty;

            var text = new StringBuilder(length + 1);
            Native.GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetClassName(IntPtr hwnd)
    {
        try
        {
            var text = new StringBuilder(256);
            Native.GetClassName(hwnd, text, text.Capacity);
            return text.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
