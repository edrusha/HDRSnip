using HDRSnip.Interop;

namespace HDRSnip.Models;

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; set; } = "";
    public Native.Rect Bounds { get; init; }
    public Native.Rect Work { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsHdr { get; set; }
    public double SdrWhiteNits { get; set; } = 80;
    public uint Dpi { get; set; } = 96;

    public double Scale => Dpi / 96.0;
    public int PixelWidth => Bounds.Width;
    public int PixelHeight => Bounds.Height;

    public string ResolutionText => $"{PixelWidth} × {PixelHeight}";
    public string Name => string.IsNullOrWhiteSpace(FriendlyName) ? DeviceName : FriendlyName;
    public string HdrText => IsHdr ? $"HDR · SDR-white {(int)Math.Round(SdrWhiteNits)} нит" : "SDR";

    public bool Contains(int x, int y) =>
        x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}
