using System.Windows.Interop;
using System.Windows.Threading;
using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip.UI;

public sealed class WindowCaptureOverlayWindow : Window
{
    private static readonly Brush HighlightTextBrush = Brushes.White;

    private readonly MonitorInfo _monitor;
    private readonly double _scale;
    private readonly BitmapSource _image;
    private readonly HighlightLayer _layer;
    private WindowHit? _hover;
    private Rect _hoverRect;
    private bool _completed;

    private double ImageWidthDip => _monitor.PixelWidth / _scale;
    private double ImageHeightDip => _monitor.PixelHeight / _scale;

    public WindowCaptureOverlayWindow(MonitorInfo monitor, BitmapSource image)
    {
        _monitor = monitor;
        _scale = Math.Max(0.5, monitor.Scale);
        _image = image;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Width = monitor.PixelWidth / _scale;
        Height = monitor.PixelHeight / _scale;
        Left = monitor.Bounds.Left / _scale;
        Top = monitor.Bounds.Top / _scale;
        Focusable = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        double imageDipWidth = monitor.PixelWidth / _scale;
        double imageDipHeight = monitor.PixelHeight / _scale;

        var root = new Grid
        {
            ClipToBounds = true,
            UseLayoutRounding = true
        };

        var imageControl = new Image
        {
            Source = image,
            Stretch = Stretch.Fill,
            Width = imageDipWidth,
            Height = imageDipHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapScalingMode(imageControl, BitmapScalingMode.HighQuality);

        _layer = new HighlightLayer(this)
        {
            Width = imageDipWidth,
            Height = imageDipHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };

        root.Children.Add(imageControl);
        root.Children.Add(_layer);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            WindowAppearance.Apply(this, rounded: false);
            PlaceWindow();
        };
        Loaded += (_, _) =>
        {
            PlaceWindow();
            Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Loaded);
            Activate();
            Focus();
            UpdateHoverFromCursor();
        };
        DpiChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Loaded);
        Closing += (_, _) => CompleteCancelIfNeeded();

        PreviewMouseMove += (_, _) => UpdateHoverFromCursor();
        MouseLeave += (_, _) => ClearHover();
        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            Cancel();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        };
    }

    public event Action<MonitorInfo, Int32Rect>? WindowSelected;
    public event Action? Cancelled;

    public MonitorInfo Monitor => _monitor;

    public void ShowOverlay()
    {
        Show();
        PlaceWindow();
    }

    private void PlaceWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        Native.SetWindowPos(
            handle,
            Native.HwndTopmost,
            _monitor.Bounds.Left,
            _monitor.Bounds.Top,
            _monitor.Bounds.Width,
            _monitor.Bounds.Height,
            0);
    }

    private void UpdateHoverFromCursor()
    {
        if (_completed || !Native.GetCursorPos(out var screen))
            return;

        var hit = WindowCaptureService.FindWindowAt(screen.X, screen.Y);
        if (hit is null)
        {
            ClearHover();
            return;
        }

        var clipped = ClipToMonitor(hit.Bounds);
        if (clipped.Width < 8 || clipped.Height < 8)
        {
            ClearHover();
            return;
        }

        var rect = new Rect(
            (clipped.Left - _monitor.Bounds.Left) / _scale,
            (clipped.Top - _monitor.Bounds.Top) / _scale,
            clipped.Width / _scale,
            clipped.Height / _scale);

        if (_hover is not null &&
            _hover.Handle == hit.Handle &&
            _hover.Title == hit.Title &&
            _hoverRect.Equals(rect))
        {
            return;
        }

        _hover = hit;
        _hoverRect = rect;
        _layer.InvalidateVisual();
    }

    private void ClearHover()
    {
        if (_hover is null && _hoverRect.IsEmpty)
            return;

        _hover = null;
        _hoverRect = Rect.Empty;
        _layer.InvalidateVisual();
    }

    private Native.Rect ClipToMonitor(Native.Rect bounds)
    {
        int left = Math.Max(bounds.Left, _monitor.Bounds.Left);
        int top = Math.Max(bounds.Top, _monitor.Bounds.Top);
        int right = Math.Min(bounds.Right, _monitor.Bounds.Right);
        int bottom = Math.Min(bounds.Bottom, _monitor.Bounds.Bottom);

        return new Native.Rect
        {
            Left = left,
            Top = top,
            Right = Math.Max(left, right),
            Bottom = Math.Max(top, bottom)
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _completed)
            return;

        e.Handled = true;

        if (!Native.GetCursorPos(out var screen))
            return;

        UpdateHoverFromCursor();

        if (_hover is null)
        {
            Log.Info($"Снимок окна: под курсором нет окна ({screen.X},{screen.Y})");
            return;
        }

        var clipped = ClipToMonitor(_hover.Bounds);
        if (clipped.Width < 8 || clipped.Height < 8)
            return;

        int x = clipped.Left - _monitor.Bounds.Left;
        int y = clipped.Top - _monitor.Bounds.Top;

        Log.Info($"Снимок окна: «{_hover.Title}» {clipped.Width}×{clipped.Height} " +
                 $"({_monitor.Name}, {x},{y})");
        Complete(new Int32Rect(x, y, clipped.Width, clipped.Height));
    }

    private void Complete(Int32Rect rect)
    {
        if (_completed)
            return;

        _completed = true;
        WindowSelected?.Invoke(_monitor, rect);
    }

    private void Cancel()
    {
        if (_completed)
            return;

        _completed = true;
        Log.Info($"Снимок окна: отмена ({_monitor.Name})");
        Cancelled?.Invoke();
    }

    private void CompleteCancelIfNeeded()
    {
        if (_completed)
            return;

        _completed = true;
        Cancelled?.Invoke();
    }

    private sealed class HighlightLayer : FrameworkElement
    {
        private static readonly Brush DimBrush = CreateDimBrush();
        private static readonly Brush LabelBackground = CreateLabelBackground();
        private static readonly Pen SelectionPen = CreateSelectionPen();
        private static readonly Typeface LabelTypeface = new(
            new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        private readonly WindowCaptureOverlayWindow _owner;

        public HighlightLayer(WindowCaptureOverlayWindow owner)
        {
            _owner = owner;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            drawingContext.DrawRectangle(DimBrush, null, new Rect(0, 0, width, height));

            if (_owner._hover is not null)
            {
                var rect = _owner._hoverRect;
                if (rect.Width > 0 && rect.Height > 0)
                {
                    // Показываем выбранное окно без затемнения — ровно то,
                    // что попадёт в снимок.
                    drawingContext.PushClip(new RectangleGeometry(rect));
                    drawingContext.DrawImage(_owner._image, new Rect(0, 0, width, height));
                    drawingContext.Pop();

                    drawingContext.DrawRectangle(null, SelectionPen, rect);
                    DrawLabel(drawingContext, _owner._hover.Title,
                        (int)Math.Round(rect.Width * _owner._scale),
                        (int)Math.Round(rect.Height * _owner._scale),
                        rect, width, height);
                }
            }

            DrawHint(drawingContext, width);
        }

        private void DrawLabel(DrawingContext drawingContext, string title, int pixelWidth,
            int pixelHeight, Rect selection, double width, double height)
        {
            string name = string.IsNullOrWhiteSpace(title) ? "Окно" : title.Trim();
            if (name.Length > 56)
                name = name[..55] + "…";

            string value = $"{name}   ·   {pixelWidth} × {pixelHeight}";
            var text = new FormattedText(value,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 12.5,
                HighlightTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double paddingX = 10;
            double paddingY = 5;
            double pillWidth = Math.Min(text.Width + paddingX * 2, width - 8);
            double pillHeight = text.Height + paddingY * 2;

            double x = selection.Left;
            double y = selection.Top - pillHeight - 7;
            if (y < 4)
                y = selection.Top + 7;

            x = Math.Clamp(x, 4, Math.Max(4, width - pillWidth - 4));
            y = Math.Clamp(y, 4, Math.Max(4, height - pillHeight - 4));

            drawingContext.DrawRoundedRectangle(LabelBackground, null,
                new Rect(x, y, pillWidth, pillHeight), 7, 7);

            text.MaxTextWidth = Math.Max(1, pillWidth - paddingX * 2);
            text.MaxTextHeight = pillHeight;
            text.Trimming = TextTrimming.CharacterEllipsis;
            drawingContext.DrawText(text, new Point(x + paddingX, y + paddingY));
        }

        private void DrawHint(DrawingContext drawingContext, double width)
        {
            const string hint = "Наведите на окно и щёлкните   ·   Esc или ПКМ — отмена";
            var text = new FormattedText(hint,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 12.5,
                HighlightTextBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double paddingX = 14;
            double paddingY = 8;
            double pillWidth = text.Width + paddingX * 2;
            double pillHeight = text.Height + paddingY * 2;
            double x = Math.Max(8, (width - pillWidth) / 2);
            const double y = 18;

            drawingContext.DrawRoundedRectangle(LabelBackground, null,
                new Rect(x, y, pillWidth, pillHeight), 9, 9);
            drawingContext.DrawText(text, new Point(x + paddingX, y + paddingY));
        }

        private static Brush CreateDimBrush()
        {
            var brush = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateLabelBackground()
        {
            var brush = new SolidColorBrush(Color.FromArgb(220, 18, 22, 30));
            brush.Freeze();
            return brush;
        }

        private static Pen CreateSelectionPen()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F));
            brush.Freeze();
            var pen = new Pen(brush, 2.2);
            pen.Freeze();
            return pen;
        }
    }
}
