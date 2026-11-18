using System.Windows.Interop;
using HDRSnip.Interop;

namespace HDRSnip.Services;

public static class WindowAppearance
{
    public static void Apply(Window window, bool rounded = true)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int dark = 1;
            Native.DwmSetWindowAttribute(handle, Native.DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            int preference = rounded ? Native.DwmwcpRound : Native.DwmwcpDoNotRound;
            Native.DwmSetWindowAttribute(handle, Native.DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // Косметика, не критично.
        }
    }

    public static void HideFromTaskbar(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int style = Native.GetWindowLong(handle, Native.GwlExStyle);
            style |= Native.WsExToolWindow;
            style &= ~Native.WsExAppWindow;
            Native.SetWindowLong(handle, Native.GwlExStyle, style);
        }
        catch
        {
            // Косметика, не критично.
        }
    }
}
