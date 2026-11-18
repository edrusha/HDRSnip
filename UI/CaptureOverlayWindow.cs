using System.Windows.Interop;
using System.Windows.Threading;
using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip.UI;

public sealed class CaptureOverlayWindow : Window
{
    private readonly MonitorInfo _monitor;
    private readonly double _scale;
    private readonly SelectionLayer _layer;
    private readonly DispatcherTimer _dragTimer;
    private Point _start;
    private Point _current;
    private bool _dragging;
    private bool _hasSelection;
    private bool _completed;

    private double ImageWidthDip => _monitor.PixelWidth / _scale;
    private double ImageHeightDip => _monitor.PixelHeight / _scale;

    public CaptureOverlayWindow(MonitorInfo monitor, BitmapSource image)
    {
        _monitor = monitor;
        _scale = Math.Max(0.5, monitor.Scale);

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

        // Размер картинки задаём в DIP явно, а не растягиваем по клиентской
        // области окна. Клиентская область может оказаться на пиксель меньше
        // монитора, и тогда Stretch.Fill пересэмплирует весь снимок — превью
        // становится мутным и «дрожит» при перерисовке выделения.
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

        _layer = new SelectionLayer(this)
        {
            Width = imageDipWidth,
            Height = imageDipHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };

        _dragTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dragTimer.Tick += (_, _) => TrackDrag();

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
            var dpi = VisualTreeHelper.GetDpi(this);
            Log.Info($"Оверлей {_monitor.Name}: клиент {ActualWidth * dpi.DpiScaleX:0.#}×{ActualHeight * dpi.DpiScaleY:0.#} px, DPI {dpi.DpiScaleX:0.###}, снимок {image.PixelWidth}×{image.PixelHeight}");
            Activate();
            Focus();
        };
        Closing += (_, _) => CompleteCancelIfNeeded();

        DpiChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Loaded);

        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        LostMouseCapture += (_, _) =>
        {
            if (!_dragging || _completed)
                return;

            // Захват может потеряться при переходе курсора на другой монитор.
            // Выделение продолжаем по таймеру, а завершаем по реальному отпусканию кнопки.
            Log.Warn($"Оверлей {_monitor.Name}: потерян захват мыши, продолжаю по таймеру");
            if ((Native.GetAsyncKeyState(0x01) & 0x8000) == 0)
                FinishDrag();
        };
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

    public event Action<MonitorInfo, Int32Rect>? RegionSelected;
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

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _completed)
            return;

        e.Handled = true;
        _dragging = true;
        _hasSelection = true;
        _start = ClampToImage(e.GetPosition(this));
        _current = _start;

        Activate();
        Focus();
        bool captured = CaptureMouse();
        if (!captured)
        {
            Activate();
            Focus();
            captured = CaptureMouse();
        }

        _dragTimer.Start();
        _layer.InvalidateVisual();
        Log.Info($"Выделение начато на {_monitor.Name}: {_start.X:0},{_start.Y:0} (capture={captured}, active={IsActive})");
    }

    private void TrackDrag()
    {
        if (!_dragging || _completed)
        {
            _dragTimer.Stop();
            return;
        }

        try
        {
            if (Native.GetCursorPos(out var screen))
            {
                var local = PointFromScreen(new Point(screen.X, screen.Y));
                _current = ClampToImage(local);
                _layer.InvalidateVisual();
            }
        }
        catch (InvalidOperationException)
        {
            // Окно уже закрывается — выделение больше не актуально.
            _dragTimer.Stop();
            return;
        }

        if ((Native.GetAsyncKeyState(0x01) & 0x8000) == 0)
        {
            Log.Info($"Кнопка мыши отпущена вне окна {_monitor.Name}, завершаю выделение");
            FinishDrag();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        _current = ClampToImage(e.GetPosition(this));
        _layer.InvalidateVisual();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;

        e.Handled = true;
        FinishDrag();
    }

    private void FinishDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;
        _dragTimer.Stop();
        ReleaseMouseCapture();
        _layer.InvalidateVisual();

        var rect = GetPixelRect();
        if (rect.Width < 8 || rect.Height < 8)
        {
            Log.Info($"Выделение слишком мало: {rect.Width}×{rect.Height}");
            _hasSelection = false;
            _layer.InvalidateVisual();
            return;
        }

        Log.Info($"Выделение готово: {rect.X},{rect.Y} {rect.Width}×{rect.Height} ({_monitor.Name})");
        Complete(rect);
    }

    private Point ClampToImage(Point point) => new(
        Math.Clamp(point.X, 0, ImageWidthDip),
        Math.Clamp(point.Y, 0, ImageHeightDip));

    private Int32Rect GetPixelRect()
    {
        double left = Math.Min(_start.X, _current.X) * _scale;
        double top = Math.Min(_start.Y, _current.Y) * _scale;
        double right = Math.Max(_start.X, _current.X) * _scale;
        double bottom = Math.Max(_start.Y, _current.Y) * _scale;

        int x = (int)Math.Floor(left);
        int y = (int)Math.Floor(top);
        int width = (int)Math.Ceiling(right) - x;
        int height = (int)Math.Ceiling(bottom) - y;

        x = Math.Clamp(x, 0, _monitor.PixelWidth - 1);
        y = Math.Clamp(y, 0, _monitor.PixelHeight - 1);
        width = Math.Clamp(width, 1, _monitor.PixelWidth - x);
        height = Math.Clamp(height, 1, _monitor.PixelHeight - y);

        return new Int32Rect(x, y, width, height);
    }

    private void Complete(Int32Rect rect)
    {
        if (_completed)
            return;

        _completed = true;
        Log.Info($"Оверлей {_monitor.Name}: область подтверждена");
        RegionSelected?.Invoke(_monitor, rect);
    }

    private void Cancel()
    {
        if (_completed)
            return;

        _completed = true;
        _dragTimer.Stop();
        Log.Info($"Оверлей {_monitor.Name}: отмена");
        Cancelled?.Invoke();
    }

    private void CompleteCancelIfNeeded()
    {
        if (_completed)
            return;

        _completed = true;
        _dragTimer.Stop();
        Cancelled?.Invoke();
    }

    private sealed class SelectionLayer : FrameworkElement
    {
        private static readonly Brush DimBrush = CreateDimBrush();
        private static readonly Brush LabelBackground = CreateLabelBackground();
        private static readonly Pen SelectionPen = CreateSelectionPen();
        private static readonly Pen GuidePen = CreateGuidePen();
        private static readonly Typeface LabelTypeface = new(
            new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        private readonly CaptureOverlayWindow _owner;

        public SelectionLayer(CaptureOverlayWindow owner)
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

            double scale = _owner._scale;
            Rect? selection = null;

            if (_owner._hasSelection)
            {
                double left = Math.Min(_owner._start.X, _owner._current.X);
                double top = Math.Min(_owner._start.Y, _owner._current.Y);
                double right = Math.Max(_owner._start.X, _owner._current.X);
                double bottom = Math.Max(_owner._start.Y, _owner._current.Y);
                selection = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            }

            // Четыре прямоугольника вместо CombinedGeometry: так WPF не
            // перестраивает тяжёлую геометрию на каждом кадре при перетаскивании.
            if (selection is { Width: > 0, Height: > 0 })
            {
                var area = selection.Value;
                DrawDim(drawingContext, new Rect(0, 0, width, area.Top));
                DrawDim(drawingContext, new Rect(0, area.Bottom, width, Math.Max(0, height - area.Bottom)));
                DrawDim(drawingContext, new Rect(0, area.Top, area.Left, area.Height));
                DrawDim(drawingContext, new Rect(area.Right, area.Top, Math.Max(0, width - area.Right), area.Height));
            }
            else
            {
                DrawDim(drawingContext, new Rect(0, 0, width, height));
            }

            if (selection is { Width: > 0, Height: > 0 })
            {
                var rect = selection.Value;
                drawingContext.DrawRectangle(null, SelectionPen, rect);

                if (_owner._dragging)
                {
                    drawingContext.DrawLine(GuidePen, new Point(rect.Left, 0), new Point(rect.Left, height));
                    drawingContext.DrawLine(GuidePen, new Point(rect.Right, 0), new Point(rect.Right, height));
                    drawingContext.DrawLine(GuidePen, new Point(0, rect.Top), new Point(width, rect.Top));
                    drawingContext.DrawLine(GuidePen, new Point(0, rect.Bottom), new Point(width, rect.Bottom));
                }

                int pixelWidth = (int)Math.Round(rect.Width * scale);
                int pixelHeight = (int)Math.Round(rect.Height * scale);
                string sizeText = $"{pixelWidth} × {pixelHeight}";
                DrawPill(drawingContext, sizeText, rect.Left, rect.Top, width, height, 11);
            }

            DrawHint(drawingContext, width);
        }

        private static void DrawDim(DrawingContext drawingContext, Rect rect)
        {
            if (rect.Width > 0 && rect.Height > 0)
                drawingContext.DrawRectangle(DimBrush, null, rect);
        }

        private void DrawHint(DrawingContext drawingContext, double width)
        {
            const string hint = "Выделите область   ·   Esc или ПКМ — отмена";
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = new FormattedText(hint, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 12.5, Brushes.White, pixelsPerDip);

            double paddingX = 14;
            double paddingY = 8;
            double pillWidth = text.Width + paddingX * 2;
            double pillHeight = text.Height + paddingY * 2;
            double x = Math.Max(8, (width - pillWidth) / 2);
            double y = 18;

            drawingContext.DrawRoundedRectangle(LabelBackground, null,
                new Rect(x, y, pillWidth, pillHeight), 9, 9);
            drawingContext.DrawText(text, new Point(x + paddingX, y + paddingY));
        }

        private void DrawPill(DrawingContext drawingContext, string value, double selectionLeft,
            double selectionTop, double width, double height, double offset)
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var text = new FormattedText(value, System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, LabelTypeface, 12.5, Brushes.White, pixelsPerDip);

            double paddingX = 8;
            double paddingY = 4;
            double pillWidth = text.Width + paddingX * 2;
            double pillHeight = text.Height + paddingY * 2;

            double x = selectionLeft;
            double y = selectionTop - pillHeight - 7;
            if (y < 4)
                y = selectionTop + 7;

            x = Math.Clamp(x, 4, Math.Max(4, width - pillWidth - 4));
            y = Math.Clamp(y, 4, Math.Max(4, height - pillHeight - 4));

            drawingContext.DrawRoundedRectangle(LabelBackground, null,
                new Rect(x, y, pillWidth, pillHeight), 6, 6);
            drawingContext.DrawText(text, new Point(x + paddingX, y + paddingY));
        }

        private static Brush CreateDimBrush()
        {
            var brush = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateLabelBackground()
        {
            var brush = new SolidColorBrush(Color.FromArgb(210, 18, 22, 30));
            brush.Freeze();
            return brush;
        }

        private static Pen CreateSelectionPen()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F));
            brush.Freeze();
            var pen = new Pen(brush, 1.6);
            pen.Freeze();
            return pen;
        }

        private static Pen CreateGuidePen()
        {
            var brush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            brush.Freeze();
            var pen = new Pen(brush, 1)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
            };
            pen.Freeze();
            return pen;
        }
    }
}
