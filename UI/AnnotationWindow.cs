using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Shapes;
using System.Windows.Threading;
using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip.UI;

public enum AnnotationAction
{
    Copy,
    Save
}

public sealed record AnnotationResult(AnnotationAction Action, BitmapSource Image);

public sealed class AnnotationWindow : Window
{
    private const double SidebarWidthDip = 200;
    private const double GapDip = 10;
    private const double PaddingDip = 14;
    private const double MinVisibleDip = 96;

    private readonly BitmapSource _image;
    private readonly MonitorInfo _monitor;
    private readonly AppSettings _settings;
    private readonly double _scale;
    private readonly double _imageDipWidth;
    private readonly double _imageDipHeight;
    private readonly double _workspaceWidthDip;
    private readonly double _workspaceHeightDip;
    private readonly AnnotationCanvas _canvas;
    private readonly Grid _imageHost;
    private readonly Border _sidebar;
    private readonly Canvas _workspace;
    private readonly List<(Border Ring, Color Color)> _colorButtons = new();
    private readonly List<(ToggleButton Button, AnnotationTool Tool)> _toolButtons = new();
    private Button _undoButton = null!;
    private Button _redoButton = null!;
    private Border _thicknessPreview = null!;
    private Slider _thicknessSlider = null!;
    private Canvas _textLayer = null!;
    private Grid? _textContainer;
    private TextBox? _textBox;
    private Point _textPoint;
    private bool _committingText;
    private bool _resizingTextInput;
    private bool _textWidthManual;
    private bool _textHeightManual;
    private UIElement? _dragElement;
    private Point _dragStart;
    private double _dragStartX;
    private double _dragStartY;
    private bool _ready;
    private bool _closing;
    private bool _suppressDeactivate;

    public AnnotationWindow(BitmapSource image, BitmapSource? backdrop, MonitorInfo monitor,
        Native.Rect screenRect, AppSettings settings)
    {
        _image = image;
        _monitor = monitor;
        _settings = settings;
        _scale = Math.Max(0.5, monitor.Scale);
        _imageDipWidth = image.PixelWidth / _scale;
        _imageDipHeight = image.PixelHeight / _scale;
        _workspaceWidthDip = monitor.PixelWidth / _scale;
        _workspaceHeightDip = monitor.PixelHeight / _scale;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // Редактор занимает весь монитор: снимок и панель — плавающие
        // элементы, которые можно двигать независимо друг от друга.
        Width = _workspaceWidthDip;
        Height = _workspaceHeightDip;
        Left = monitor.Bounds.Left / _scale;
        Top = monitor.Bounds.Top / _scale;

        var root = new Grid
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12)),
            ClipToBounds = true
        };

        if (backdrop is not null)
        {
            // Приглушённый снимок монитора на фоне: снимок выглядит парящим
            // над тем местом, где он был выделен.
            var backdropImage = new Image
            {
                Source = backdrop,
                Stretch = Stretch.Fill,
                Opacity = 0.34,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(backdropImage, BitmapScalingMode.HighQuality);
            root.Children.Add(backdropImage);
        }

        _workspace = new Canvas
        {
            Background = Brushes.Transparent,
            ClipToBounds = true
        };
        root.Children.Add(_workspace);

        var imageControl = new Image
        {
            Source = image,
            Stretch = Stretch.Fill,
            Width = _imageDipWidth,
            Height = _imageDipHeight,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(imageControl, BitmapScalingMode.HighQuality);

        _canvas = new AnnotationCanvas
        {
            Width = _imageDipWidth,
            Height = _imageDipHeight,
            ImageScale = _scale,
            Thickness = Math.Clamp(settings.AnnotationThickness, 1, 24),
            StrokeColor = ParseColor(settings.AnnotationColor, Color.FromRgb(0xFF, 0x4D, 0x4F))
        };
        _canvas.SetSourceImage(image);

        _imageHost = new Grid
        {
            Width = _imageDipWidth,
            Height = _imageDipHeight,
            Background = Brushes.Transparent,
            ClipToBounds = true,
            Cursor = Cursors.Arrow
        };
        _imageHost.Children.Add(imageControl);
        _imageHost.Children.Add(_canvas);

        // Слой для поля ввода текста. Он лежит поверх рисунка, но не мешает
        // рисовать, пока поле ввода закрыто.
        _textLayer = new Canvas
        {
            Width = _imageDipWidth,
            Height = _imageDipHeight,
            Background = null,
            IsHitTestVisible = true
        };
        _imageHost.Children.Add(_textLayer);

        _sidebar = BuildSidebar();
        _sidebar.Width = SidebarWidthDip;
        _sidebar.Cursor = Cursors.SizeAll;
        _sidebar.Measure(new Size(SidebarWidthDip, double.PositiveInfinity));

        double imageX = (screenRect.Left - monitor.Bounds.Left) / _scale;
        double imageY = (screenRect.Top - monitor.Bounds.Top) / _scale;
        imageX = ClampFullyVisible(imageX, _imageDipWidth, _workspaceWidthDip);
        imageY = ClampFullyVisible(imageY, _imageDipHeight, _workspaceHeightDip);

        double sidebarHeight = Math.Max(0, _sidebar.DesiredSize.Height);
        bool sideRight = imageX + _imageDipWidth + GapDip + SidebarWidthDip + PaddingDip <= _workspaceWidthDip;
        bool sideLeft = !sideRight && imageX - GapDip - SidebarWidthDip - PaddingDip >= 0;

        double sidebarX = sideRight
            ? imageX + _imageDipWidth + GapDip
            : sideLeft
                ? imageX - GapDip - SidebarWidthDip
                : imageX + _imageDipWidth - SidebarWidthDip - GapDip;

        sidebarX = Math.Clamp(sidebarX, PaddingDip,
            Math.Max(PaddingDip, _workspaceWidthDip - SidebarWidthDip - PaddingDip));
        double sidebarY = Math.Clamp(imageY, PaddingDip,
            Math.Max(PaddingDip, _workspaceHeightDip - sidebarHeight - PaddingDip));

        Canvas.SetLeft(_imageHost, imageX);
        Canvas.SetTop(_imageHost, imageY);
        Canvas.SetLeft(_sidebar, sidebarX);
        Canvas.SetTop(_sidebar, sidebarY);
        Panel.SetZIndex(_sidebar, 1);

        _workspace.Children.Add(_imageHost);
        _workspace.Children.Add(_sidebar);
        Content = root;

        Log.Info($"Редактор: изображение {image.PixelWidth}×{image.PixelHeight}, поле {_workspaceWidthDip:0.#}×{_workspaceHeightDip:0.#} DIP @ {imageX:0.#},{imageY:0.#} (scale {_scale:0.##}, панель {(sideRight ? "справа" : sideLeft ? "слева" : "поверх")})");

        root.MouseLeftButtonDown += Root_MouseLeftButtonDown;
        _imageHost.PreviewMouseDown += ImageHost_PreviewMouseDown;
        _sidebar.PreviewMouseLeftButtonDown += Sidebar_PreviewMouseLeftButtonDown;
        PreviewMouseMove += OnWindowPreviewMouseMove;
        PreviewMouseUp += OnWindowPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;

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
            Log.Info($"Редактор: клиент {ActualWidth * dpi.DpiScaleX:0.#}×{ActualHeight * dpi.DpiScaleY:0.#} px, DPI {dpi.DpiScaleX:0.###}");

            Activate();
            Focus();
            Keyboard.Focus(_canvas);
        };
        Activated += (_, _) =>
        {
            if (_ready)
                return;

            Dispatcher.BeginInvoke(new Action(() => _ready = true), DispatcherPriority.ApplicationIdle);
        };
        DpiChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(PlaceWindow), DispatcherPriority.Loaded);
        Deactivated += OnDeactivated;
        Closing += (_, _) =>
        {
            _closing = true;
            PersistDrawingSettings();
        };
        PreviewKeyDown += OnPreviewKeyDown;

        _canvas.TextRequested += BeginTextInput;
        _canvas.HistoryChanged += UpdateHistoryButtons;

        SelectTool(ParseTool(settings.AnnotationTool));
        UpdateHistoryButtons();
    }

    public AnnotationResult? Result { get; private set; }

    public BitmapSource ExportComposite()
    {
        int width = _image.PixelWidth;
        int height = _image.PixelHeight;
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.DrawImage(_image, new Rect(0, 0, width, height));
            _canvas.DrawAnnotations(context, coordinateDivisor: 1.0, thicknessMultiplier: _scale);
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    private void PlaceWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        Native.SetWindowPos(handle, Native.HwndTopmost,
            _monitor.Bounds.Left, _monitor.Bounds.Top,
            _monitor.Bounds.Width, _monitor.Bounds.Height, 0);
    }

    private Border BuildSidebar()
    {
        Brush text = (Brush)FindResource("TextBrush");
        Brush muted = (Brush)FindResource("MutedBrush");
        Brush stroke = (Brush)FindResource("StrokeBrush");
        var hoverBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        hoverBrush.Freeze();

        var grip = new Border
        {
            Height = 16,
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            ToolTip = "Перетащите панель"
        };
        grip.Child = new Border
        {
            Width = 36,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = muted,
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        row1.Children.Add(CreateToolButton(AnnotationTool.Brush,
            Glyph("\uE70F", 16, text), "Кисть (1)", new Thickness(0)));

        var lineGlyph = Glyph("\uE738", 16, text);
        lineGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
        lineGlyph.RenderTransform = new RotateTransform(-45);
        row1.Children.Add(CreateToolButton(AnnotationTool.Line,
            lineGlyph, "Прямая линия (2)", new Thickness(4, 0, 0, 0)));
        row1.Children.Add(CreateToolButton(AnnotationTool.Arrow,
            Glyph("\uE72A", 16, text), "Стрелка (3)", new Thickness(4, 0, 0, 0)));
        row1.Children.Add(CreateToolButton(AnnotationTool.Move,
            Glyph("\uE7C2", 16, text),
            "Перемещение (4). Снимок можно двигать и средней кнопкой мыши.",
            new Thickness(4, 0, 0, 0)));

        var row2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        };
        row2.Children.Add(CreateToolButton(AnnotationTool.Rectangle,
            ShapeIcon(text, ellipse: false), "Прямоугольник (5)", new Thickness(0)));
        row2.Children.Add(CreateToolButton(AnnotationTool.Ellipse,
            ShapeIcon(text, ellipse: true), "Овал (6)", new Thickness(4, 0, 0, 0)));
        row2.Children.Add(CreateToolButton(AnnotationTool.Highlighter,
            HighlighterIcon(text), "Маркер (7)", new Thickness(4, 0, 0, 0)));
        row2.Children.Add(CreateToolButton(AnnotationTool.Text,
            TextIcon(text), "Текст (8). Щёлкните по снимку и введите текст.",
            new Thickness(4, 0, 0, 0)));
        row2.Children.Add(CreateToolButton(AnnotationTool.Blur,
            BlurIcon(muted), "Размытие (9). Скрывает пароли и личные данные.",
            new Thickness(4, 0, 0, 0)));

        _undoButton = HistoryButton("\uE7A7", "Отменить (Ctrl+Z)", new Thickness(0));
        _undoButton.Click += (_, _) => _canvas.Undo();

        _redoButton = HistoryButton("\uE7A6", "Повторить (Ctrl+Y)", new Thickness(4, 0, 0, 0));
        _redoButton.Click += (_, _) => _canvas.Redo();

        var history = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        history.Children.Add(_undoButton);
        history.Children.Add(_redoButton);

        var tools = new StackPanel();
        tools.Children.Add(row1);
        tools.Children.Add(row2);
        tools.Children.Add(history);

        var colorGrid = new UniformGrid
        {
            Columns = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var colors = new[]
        {
            Color.FromRgb(0xFF, 0x4D, 0x4F),
            Color.FromRgb(0xFF, 0xB0, 0x20),
            Color.FromRgb(0x35, 0xD0, 0x7F),
            Color.FromRgb(0x4D, 0xA3, 0xFF),
            Color.FromRgb(0xB5, 0x7B, 0xFF),
            Colors.White,
            Color.FromRgb(0x12, 0x14, 0x18)
        };

        foreach (var color in colors)
        {
            var dot = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(color),
                BorderBrush = color == Color.FromRgb(0x12, 0x14, 0x18)
                    ? new SolidColorBrush(Color.FromRgb(0x59, 0x65, 0x7A))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(1)
            };

            // Фон обязателен: без него Border попадает под курсор только
            // внутри дочерней точки, и приходится целиться в самый центр.
            var ring = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Child = dot,
                Cursor = Cursors.Hand,
                ToolTip = "Цвет",
                Tag = "NoDrag",
                Margin = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            ring.MouseEnter += (_, _) => ring.Background = hoverBrush;
            ring.MouseLeave += (_, _) => ring.Background = Brushes.Transparent;

            var capturedColor = color;
            ring.MouseLeftButtonDown += (_, e) =>
            {
                _canvas.StrokeColor = capturedColor;
                UpdateColorSelection();
                e.Handled = true;
            };

            _colorButtons.Add((ring, capturedColor));
            colorGrid.Children.Add(ring);
        }

        var thicknessHeader = new Grid
        {
            Margin = new Thickness(0, 0, 0, 2)
        };
        thicknessHeader.ColumnDefinitions.Add(new ColumnDefinition());
        thicknessHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thicknessValue = new TextBlock
        {
            Text = _canvas.Thickness.ToString("0.#"),
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };

        var thicknessLabel = new TextBlock
        {
            Text = "Толщина",
            FontSize = 11,
            Foreground = muted,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(thicknessValue, 1);
        thicknessHeader.Children.Add(thicknessLabel);
        thicknessHeader.Children.Add(thicknessValue);

        _thicknessPreview = new Border
        {
            Background = new SolidColorBrush(_canvas.StrokeColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var previewHost = new Grid
        {
            Width = 26,
            Height = 26
        };
        previewHost.Children.Add(_thicknessPreview);

        _thicknessSlider = new Slider
        {
            Style = (Style)FindResource("SubtleSlider"),
            Minimum = 1,
            Maximum = 24,
            SmallChange = 0.5,
            LargeChange = 2,
            TickFrequency = 0.5,
            IsSnapToTickEnabled = true,
            Value = _canvas.Thickness,
            Tag = "NoDrag",
            ToolTip = "Толщина: 1–24. Колесо мыши тоже меняет толщину.",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        _thicknessSlider.ValueChanged += (_, e) =>
        {
            _canvas.Thickness = e.NewValue;
            thicknessValue.Text = e.NewValue.ToString("0.#");
            UpdateThicknessPreview();
        };

        var thicknessRow = new Grid();
        thicknessRow.ColumnDefinitions.Add(new ColumnDefinition());
        thicknessRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        thicknessRow.Children.Add(_thicknessSlider);
        Grid.SetColumn(previewHost, 1);
        thicknessRow.Children.Add(previewHost);

        var thicknessPanel = new StackPanel();
        thicknessPanel.Children.Add(thicknessHeader);
        thicknessPanel.Children.Add(thicknessRow);

        var copyButton = new Button
        {
            Style = (Style)FindResource("AccentButton"),
            Width = 58,
            Height = 42,
            Content = Glyph("\uE8C8", 17, Brushes.White),
            ToolTip = "Копировать в буфер (Ctrl+C)"
        };
        copyButton.Click += (_, _) => Finish(AnnotationAction.Copy);

        var saveButton = new Button
        {
            Style = (Style)FindResource("SecondaryButton"),
            Width = 58,
            Height = 42,
            Content = Glyph("\uE74E", 17, text),
            ToolTip = "Сохранить PNG (Ctrl+S)",
            Margin = new Thickness(8, 0, 0, 0)
        };
        saveButton.Click += (_, _) => Finish(AnnotationAction.Save);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        actions.Children.Add(copyButton);
        actions.Children.Add(saveButton);

        var stack = new StackPanel();
        stack.Children.Add(grip);
        stack.Children.Add(tools);
        stack.Children.Add(SidebarSeparator(stroke));
        stack.Children.Add(colorGrid);
        stack.Children.Add(SidebarSeparator(stroke));
        stack.Children.Add(thicknessPanel);
        stack.Children.Add(SidebarSeparator(stroke));
        stack.Children.Add(actions);

        UpdateColorSelection();
        UpdateThicknessPreview();

        return new Border
        {
            Width = SidebarWidthDip,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x22)),
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            Child = stack
        };
    }

    private ToggleButton CreateToolButton(AnnotationTool tool, object content, string tooltip, Thickness margin)
    {
        var button = new ToggleButton
        {
            Style = (Style)FindResource("ToolToggle"),
            Width = 32,
            Height = 36,
            Content = content,
            ToolTip = tooltip,
            Margin = margin
        };

        button.Click += (_, _) => SelectTool(tool);
        _toolButtons.Add((button, tool));
        return button;
    }

    private Button HistoryButton(string glyph, string tooltip, Thickness margin) => new()
    {
        Style = (Style)FindResource("IconButton"),
        Width = 32,
        Height = 34,
        Padding = new Thickness(0),
        Content = Glyph(glyph, 15, (Brush)FindResource("MutedBrush")),
        ToolTip = tooltip,
        Margin = margin
    };

    private static FrameworkElement ShapeIcon(Brush stroke, bool ellipse)
    {
        if (ellipse)
        {
            return new Ellipse
            {
                Width = 17,
                Height = 12,
                Stroke = stroke,
                StrokeThickness = 1.6
            };
        }

        return new Rectangle
        {
            Width = 16,
            Height = 12,
            RadiusX = 2,
            RadiusY = 2,
            Stroke = stroke,
            StrokeThickness = 1.6
        };
    }

    private static FrameworkElement HighlighterIcon(Brush stroke)
    {
        var marker = new Border
        {
            Width = 17,
            Height = 7,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xB0, 0x20)),
            BorderBrush = stroke,
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-45)
        };

        return new Grid { Width = 18, Height = 18, Children = { marker } };
    }

    private static FrameworkElement TextIcon(Brush brush) => new TextBlock
    {
        Text = "A",
        FontSize = 17,
        FontWeight = FontWeights.Bold,
        Foreground = brush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static FrameworkElement BlurIcon(Brush brush)
    {
        var grid = new UniformGrid
        {
            Rows = 3,
            Columns = 3,
            Width = 16,
            Height = 16
        };

        foreach (double opacity in new[] { 0.25, 0.6, 0.4, 0.75, 0.35, 0.65, 0.45, 0.8, 0.5 })
        {
            grid.Children.Add(new Border
            {
                Background = brush,
                Opacity = opacity,
                Margin = new Thickness(0.6)
            });
        }

        return grid;
    }

    private static Border SidebarSeparator(Brush stroke) => new()
    {
        Height = 1,
        Background = stroke,
        Margin = new Thickness(0, 10, 0, 10)
    };

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _closing)
            return;

        var source = e.OriginalSource as DependencyObject;
        if (IsInside(source, _imageHost) || IsInside(source, _sidebar))
            return;

        e.Handled = true;
        Close();
    }

    private void ImageHost_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInside(e.OriginalSource as DependencyObject, _textLayer))
            return;

        // Клик мимо поля ввода завершает ввод, даже если фокус не успел
        // перейти из-за перетаскивания ручки изменения размера.
        if (_textBox is not null)
        {
            _resizingTextInput = false;
            CommitTextInput();
        }

        bool moveWithLeft = e.ChangedButton == MouseButton.Left &&
                            (_canvas.Tool == AnnotationTool.Move || Keyboard.IsKeyDown(Key.Space));

        if (e.ChangedButton != MouseButton.Middle && !moveWithLeft)
            return;

        // Инструментом «Перемещение» текст двигает сам холст:
        // он показывает рамку и меняет положение надписи.
        if (e.ChangedButton == MouseButton.Left &&
            _canvas.Tool == AnnotationTool.Move &&
            !Keyboard.IsKeyDown(Key.Space) &&
            _canvas.HitTestText(e.GetPosition(_canvas)))
        {
            return;
        }

        BeginDrag(_imageHost, e);
        e.Handled = true;
    }

    private void Sidebar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractive(e.OriginalSource as DependencyObject))
            return;

        BeginDrag(_sidebar, e);
        e.Handled = true;
    }

    private void BeginDrag(UIElement element, MouseButtonEventArgs e)
    {
        if (_dragElement is not null)
            return;

        _dragElement = element;
        _dragStart = e.GetPosition(_workspace);
        _dragStartX = GetCanvasCoordinate(Canvas.GetLeft(element));
        _dragStartY = GetCanvasCoordinate(Canvas.GetTop(element));

        if (!element.CaptureMouse())
            Mouse.Capture(element, CaptureMode.Element);

        if (ReferenceEquals(element, _imageHost))
            _canvas.Cursor = Cursors.SizeAll;

        Activate();
        Focus();
    }

    private void OnWindowPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragElement is null)
            return;

        var point = e.GetPosition(_workspace);
        double left = _dragStartX + point.X - _dragStart.X;
        double top = _dragStartY + point.Y - _dragStart.Y;

        if (ReferenceEquals(_dragElement, _imageHost))
        {
            left = ClampImageCoordinate(left, _imageDipWidth, _workspaceWidthDip);
            top = ClampImageCoordinate(top, _imageDipHeight, _workspaceHeightDip);
        }
        else
        {
            left = ClampPanelCoordinate(left, _sidebar.Width, _workspaceWidthDip);
            top = ClampPanelCoordinate(top, GetSidebarHeight(), _workspaceHeightDip);
        }

        Canvas.SetLeft(_dragElement, left);
        Canvas.SetTop(_dragElement, top);
        e.Handled = true;
    }

    private void OnWindowPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragElement is null)
            return;

        if (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Middle)
            return;

        var element = _dragElement;
        _dragElement = null;
        element.ReleaseMouseCapture();
        _canvas.Cursor = _canvas.Tool == AnnotationTool.Move ? Cursors.SizeAll : Cursors.Cross;
        e.Handled = true;
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_closing || e.Delta == 0 || _textBox is not null)
            return;

        // Колесо работает в любом месте редактора, не только над ползунком.
        // Shift — более точный шаг. В других окнах приложения колесо
        // обрабатывается как обычно: здесь оно перехватывается только
        // в окне редактора.
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.5 : 1.0;
        double next = Math.Clamp(_canvas.Thickness + Math.Sign(e.Delta) * step, 1, 24);

        if (Math.Abs(next - _canvas.Thickness) > 0.001)
            _thicknessSlider.Value = next;

        e.Handled = true;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_ready || _closing || _suppressDeactivate)
            return;

        Log.Info("Редактор: потерян фокус, закрываю");
        _closing = true;
        Close();
    }

    private double GetSidebarHeight()
    {
        double height = _sidebar.ActualHeight;
        if (height <= 0)
            height = _sidebar.DesiredSize.Height;

        return height > 0 ? height : SidebarWidthDip;
    }

    private static double ClampFullyVisible(double coordinate, double size, double workspace)
    {
        if (size >= workspace)
            return Math.Clamp(coordinate, workspace - size, 0);

        return Math.Clamp(coordinate, 0, workspace - size);
    }

    private static double ClampImageCoordinate(double coordinate, double size, double workspace)
    {
        double visible = Math.Min(MinVisibleDip, size);
        double min = -(size - visible);
        double max = workspace - visible;

        return max < min ? 0 : Math.Clamp(coordinate, min, max);
    }

    private static double ClampPanelCoordinate(double coordinate, double size, double workspace)
    {
        if (size >= workspace)
            return 0;

        return Math.Clamp(coordinate, 0, workspace - size);
    }

    private static double GetCanvasCoordinate(double value) =>
        double.IsNaN(value) ? 0 : value;

    private static bool IsInteractive(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or Thumb)
                return true;

            if (source is FrameworkElement { Tag: "NoDrag" })
                return true;

            source = GetParent(source);
        }

        return false;
    }

    private static bool IsInside(DependencyObject? source, DependencyObject parent)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, parent))
                return true;

            source = GetParent(source);
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject source) =>
        source is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(source)
            : LogicalTreeHelper.GetParent(source);

    private void SelectTool(AnnotationTool tool)
    {
        _canvas.Tool = tool;

        if (tool != AnnotationTool.Move)
            _canvas.ClearSelection();

        foreach (var (button, buttonTool) in _toolButtons)
            button.IsChecked = buttonTool == tool;

        _canvas.Cursor = tool == AnnotationTool.Move ? Cursors.SizeAll : Cursors.Cross;
        Keyboard.Focus(_canvas);
    }

    private void UpdateHistoryButtons()
    {
        if (_undoButton is not null)
            _undoButton.IsEnabled = _canvas.CanUndo;

        if (_redoButton is not null)
            _redoButton.IsEnabled = _canvas.CanRedo;
    }

    private void UpdateColorSelection()
    {
        Brush accent = (Brush)FindResource("AccentBrush");
        foreach (var (ring, color) in _colorButtons)
            ring.BorderBrush = color == _canvas.StrokeColor ? accent : Brushes.Transparent;

        UpdateThicknessPreview();
    }

    private void UpdateThicknessPreview()
    {
        if (_thicknessPreview is null)
            return;

        double diameter = Math.Clamp(_canvas.Thickness + 2, 4, 22);
        _thicknessPreview.Width = diameter;
        _thicknessPreview.Height = diameter;
        _thicknessPreview.CornerRadius = new CornerRadius(diameter / 2);

        var brush = new SolidColorBrush(_canvas.StrokeColor);
        brush.Freeze();
        _thicknessPreview.Background = brush;
    }

    private void BeginTextInput(Point imagePoint)
    {
        if (_closing)
            return;

        CommitTextInput();
        _textPoint = imagePoint;
        _resizingTextInput = false;
        _textWidthManual = false;
        _textHeightManual = false;

        double fontSize = Math.Max(14.0, _canvas.Thickness * 2.2);
        const double minWidth = 40;
        double minHeight = Math.Max(24, fontSize * 1.2);
        double width = Math.Min(280, Math.Max(minWidth, _imageDipWidth));
        double left = Math.Clamp(imagePoint.X / _scale, 0, Math.Max(0, _imageDipWidth - width));
        double top = Math.Clamp(imagePoint.Y / _scale, 0,
            Math.Max(0, _imageDipHeight - fontSize * 1.6));
        double maxWidth = Math.Max(minWidth, _imageDipWidth - left);
        double maxHeight = Math.Max(minHeight, _imageDipHeight - top);
        width = Math.Min(width, maxWidth);

        var color = _canvas.StrokeColor;
        var accent = new SolidColorBrush(color);
        accent.Freeze();

        var box = new TextBox
        {
            MinHeight = fontSize * 1.6,
            FontFamily = FontFamily,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0D, 0x12)),
            BorderBrush = accent,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(6, 3, 6, 3),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CaretBrush = accent,
            SelectionBrush = accent,
            Tag = "NoDrag"
        };

        // Поле ввода живёт внутри контейнера: за ручки справа, снизу и в углу
        // можно менять ширину и высоту будущей надписи ещё до её создания.
        var container = new Grid
        {
            Width = width,
            MaxHeight = maxHeight,
            Tag = "NoDrag"
        };
        container.Children.Add(box);

        var rightHandle = CreateTextResizeThumb(accent, Cursors.SizeWE, 8, 28);
        rightHandle.HorizontalAlignment = HorizontalAlignment.Right;
        rightHandle.VerticalAlignment = VerticalAlignment.Center;
        rightHandle.Margin = new Thickness(0, 0, -4, 0);

        var bottomHandle = CreateTextResizeThumb(accent, Cursors.SizeNS, 28, 8);
        bottomHandle.HorizontalAlignment = HorizontalAlignment.Center;
        bottomHandle.VerticalAlignment = VerticalAlignment.Bottom;
        bottomHandle.Margin = new Thickness(0, 0, 0, -4);

        var cornerHandle = CreateTextResizeThumb(accent, Cursors.SizeNWSE, 12, 12);
        cornerHandle.HorizontalAlignment = HorizontalAlignment.Right;
        cornerHandle.VerticalAlignment = VerticalAlignment.Bottom;
        cornerHandle.Margin = new Thickness(0, 0, -6, -6);

        container.Children.Add(rightHandle);
        container.Children.Add(bottomHandle);
        container.Children.Add(cornerHandle);

        void ResizeWidth(double delta)
        {
            _textWidthManual = true;
            container.Width = Math.Clamp(container.Width + delta, minWidth, maxWidth);
        }

        void ResizeHeight(double delta)
        {
            _textHeightManual = true;
            double current = double.IsNaN(container.Height) ? container.ActualHeight : container.Height;
            container.Height = Math.Clamp(current + delta, minHeight, maxHeight);
        }

        void FinishResize()
        {
            _resizingTextInput = false;

            if (!_closing)
                box.Focus();
        }

        void StartHeightResize()
        {
            _resizingTextInput = true;

            if (double.IsNaN(container.Height))
                container.Height = Math.Max(minHeight, container.ActualHeight);
        }

        foreach (var handle in new[] { rightHandle, bottomHandle, cornerHandle })
        {
            // Фокус не должен уходить из поля ввода во время перетаскивания
            // ручки, иначе текст зафиксируется раньше времени.
            handle.PreviewMouseLeftButtonDown += (_, _) => _resizingTextInput = true;
        }

        rightHandle.DragStarted += (_, _) => _resizingTextInput = true;
        rightHandle.DragDelta += (_, e) => ResizeWidth(e.HorizontalChange);
        rightHandle.DragCompleted += (_, _) => FinishResize();

        bottomHandle.DragStarted += (_, _) => StartHeightResize();
        bottomHandle.DragDelta += (_, e) => ResizeHeight(e.VerticalChange);
        bottomHandle.DragCompleted += (_, _) => FinishResize();

        cornerHandle.DragStarted += (_, _) => StartHeightResize();
        cornerHandle.DragDelta += (_, e) =>
        {
            ResizeWidth(e.HorizontalChange);
            ResizeHeight(e.VerticalChange);
        };
        cornerHandle.DragCompleted += (_, _) => FinishResize();

        box.PreviewKeyDown += (_, e) =>
        {
            bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (control && e.Key == Key.Enter)
            {
                e.Handled = true;
                CommitTextInput();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CancelTextInput();
            }
        };

        // Клик по панели или другому месту снимка завершает ввод,
        // чтобы текст не потерялся. Во время изменения размера фокус
        // намеренно не считается потерянным.
        box.LostKeyboardFocus += (_, _) =>
        {
            if (!_resizingTextInput)
                CommitTextInput();
        };

        _textContainer = container;
        _textBox = box;
        _textLayer.Children.Add(container);
        Canvas.SetLeft(container, left);
        Canvas.SetTop(container, top);

        box.Focus();
        Keyboard.Focus(box);
        box.CaretIndex = box.Text.Length;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_textBox, box))
                Keyboard.Focus(box);
        }), DispatcherPriority.Input);
    }

    private static Thumb CreateTextResizeThumb(Brush border, Cursor cursor, double width, double height) => new()
    {
        Width = width,
        Height = height,
        Background = Brushes.White,
        BorderBrush = border,
        BorderThickness = new Thickness(1.5),
        Cursor = cursor,
        Focusable = false,
        Tag = "NoDrag"
    };

    private void CommitTextInput()
    {
        if (_textBox is null || _committingText)
            return;

        _committingText = true;

        try
        {
            var box = _textBox;
            var container = _textContainer;
            _textBox = null;
            _textContainer = null;

            string text = box.Text.TrimEnd();

            int explicitLines = 1;
            foreach (char c in text)
            {
                if (c == '\n')
                    explicitLines++;
            }

            // Если при вводе текст переносился по ширине поля, сохраняем
            // эту ширину: готовая надпись будет выглядеть так же.
            bool wrapped = box.LineCount > explicitLines;

            double widthDip = container is null
                ? 0
                : double.IsNaN(container.Width) ? container.ActualWidth : container.Width;
            double heightDip = container is null
                ? 0
                : double.IsNaN(container.Height) ? container.ActualHeight : container.Height;
            bool manualWidth = _textWidthManual || wrapped;
            bool manualHeight = _textHeightManual;
            _textWidthManual = false;
            _textHeightManual = false;
            _resizingTextInput = false;

            if (container is not null)
                _textLayer.Children.Remove(container);
            else
                _textLayer.Children.Remove(box);

            if (!_closing && !string.IsNullOrWhiteSpace(text))
            {
                var stroke = new AnnotationStroke
                {
                    Tool = AnnotationTool.Text,
                    Color = _canvas.StrokeColor,
                    Thickness = _canvas.Thickness,
                    Text = text,
                    TextWidth = manualWidth ? widthDip * _scale : 0,
                    TextHeight = manualHeight ? heightDip * _scale : 0
                };
                stroke.Points.Add(_textPoint);
                _canvas.AddStroke(stroke);
            }
        }
        finally
        {
            _committingText = false;

            if (!_closing)
            {
                _canvas.Focus();
                Keyboard.Focus(_canvas);
            }
        }
    }

    private void CancelTextInput()
    {
        if (_textBox is null || _committingText)
            return;

        _committingText = true;

        try
        {
            var box = _textBox;
            var container = _textContainer;
            _textBox = null;
            _textContainer = null;
            _textWidthManual = false;
            _textHeightManual = false;
            _resizingTextInput = false;

            if (container is not null)
                _textLayer.Children.Remove(container);
            else
                _textLayer.Children.Remove(box);
        }
        finally
        {
            _committingText = false;

            if (!_closing)
            {
                _canvas.Focus();
                Keyboard.Focus(_canvas);
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Пока открыто поле ввода, все клавиши принадлежат ему:
        // Ctrl+Enter — готово, Esc — отмена, остальное — набор текста.
        if (_textBox is not null)
            return;

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (control && e.Key == Key.C)
        {
            e.Handled = true;
            Finish(AnnotationAction.Copy);
            return;
        }

        if (control && e.Key == Key.S)
        {
            e.Handled = true;
            Finish(AnnotationAction.Save);
            return;
        }

        if (control && e.Key == Key.Z)
        {
            e.Handled = true;

            if (shift)
                _canvas.Redo();
            else
                _canvas.Undo();

            return;
        }

        if (control && e.Key == Key.Y)
        {
            e.Handled = true;
            _canvas.Redo();
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        if (e.Key is Key.D1 or Key.NumPad1)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Brush);
        }
        else if (e.Key is Key.D2 or Key.NumPad2)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Line);
        }
        else if (e.Key is Key.D3 or Key.NumPad3)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Arrow);
        }
        else if (e.Key is Key.D4 or Key.NumPad4)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Move);
        }
        else if (e.Key is Key.D5 or Key.NumPad5)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Rectangle);
        }
        else if (e.Key is Key.D6 or Key.NumPad6)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Ellipse);
        }
        else if (e.Key is Key.D7 or Key.NumPad7)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Highlighter);
        }
        else if (e.Key is Key.D8 or Key.NumPad8)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Text);
        }
        else if (e.Key is Key.D9 or Key.NumPad9)
        {
            e.Handled = true;
            SelectTool(AnnotationTool.Blur);
        }
    }

    private void Finish(AnnotationAction action)
    {
        try
        {
            CommitTextInput();
            Log.Info($"Редактор: действие {action}");
            var image = ExportComposite();
            Result = new AnnotationResult(action, image);
            Close();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось подготовить изображение", ex);
            _suppressDeactivate = true;
            try
            {
                System.Windows.MessageBox.Show(this,
                    "Не удалось подготовить изображение: " + ex.Message,
                    "HDR Snip",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _suppressDeactivate = false;
                Activate();
                Keyboard.Focus(_canvas);
            }
        }
    }

    private void PersistDrawingSettings()
    {
        try
        {
            // Инструмент «Перемещение» не относится к рисованию: если он был
            // выбран при сохранении, в следующий раз откроется кисть.
            AnnotationTool tool = _canvas.Tool == AnnotationTool.Move
                ? AnnotationTool.Brush
                : _canvas.Tool;

            var color = _canvas.StrokeColor;
            _settings.AnnotationTool = tool.ToString();
            _settings.AnnotationColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _settings.AnnotationThickness = Math.Clamp(_canvas.Thickness, 1, 24);
            SettingsService.Save(_settings);
            Log.Info($"Редактор: настройки рисования сохранены ({tool}, {_settings.AnnotationColor}, {_settings.AnnotationThickness:0.#})");
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось сохранить настройки рисования: " + ex.Message);
        }
    }

    private static AnnotationTool ParseTool(string? value)
    {
        if (Enum.TryParse<AnnotationTool>(value, ignoreCase: true, out var tool) &&
            tool != AnnotationTool.Move)
        {
            return tool;
        }

        return AnnotationTool.Brush;
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return fallback;
        }
    }

    private static TextBlock Glyph(string text, double size, Brush brush, double marginRight = 0) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = brush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, marginRight, 0)
    };
}
