using System.Globalization;
using System.Windows.Media;

namespace HDRSnip.UI;

public enum AnnotationTool
{
    Brush,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Highlighter,
    Text,
    Blur,
    Move
}

public sealed class AnnotationStroke
{
    public AnnotationTool Tool { get; init; }
    public Color Color { get; init; }
    public double Thickness { get; init; }
    public string? Text { get; init; }
    public double TextWidth { get; set; }
    public double TextHeight { get; set; }
    public List<Point> Points { get; } = new();
}

public sealed class AnnotationCanvas : FrameworkElement
{
    private static readonly Typeface TextTypeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Brush SelectionFill = CreateSelectionFill();
    private static readonly Pen SelectionPen = CreateSelectionPen();
    private static readonly Pen SelectionHandlePen = CreateSelectionHandlePen();

    private enum TextResizeHandle
    {
        None,
        Right,
        Bottom,
        Corner
    }

    private readonly List<AnnotationStroke> _strokes = new();
    private readonly List<AnnotationStroke> _redo = new();
    private AnnotationStroke? _current;
    private AnnotationStroke? _selectedText;
    private bool _drawing;
    private bool _movingText;
    private bool _resizingText;
    private TextResizeHandle _resizeHandle;
    private Point _resizeStart;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private Point _rawPoint;
    private Point _moveStart;
    private Point _moveOrigin;
    private BitmapSource? _sourceImage;
    private byte[]? _sourcePixels;
    private int _sourceWidth;
    private int _sourceHeight;
    private int _sourceStride;

    public AnnotationCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    /// <summary>Запрос на ввод текста в точке снимка (координаты в пикселях изображения).</summary>
    public event Action<Point>? TextRequested;

    /// <summary>Изменилось состояние отмены/повтора.</summary>
    public event Action? HistoryChanged;

    /// <summary>Сколько физических пикселей приходится на один DIP.</summary>
    public double ImageScale { get; set; } = 1.0;

    public AnnotationTool Tool { get; set; } = AnnotationTool.Brush;
    public Color StrokeColor { get; set; } = Color.FromRgb(0xFF, 0x5A, 0x5F);
    public double Thickness { get; set; } = 5;

    public bool HasStrokes => _strokes.Count > 0;
    public bool CanUndo => _strokes.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void SetSourceImage(BitmapSource image) => _sourceImage = image;

    /// <summary>Попадает ли точка (в DIP холста) в какую-нибудь надпись.</summary>
    public bool HitTestText(Point canvasPoint)
    {
        var imagePoint = ToImagePoint(canvasPoint);

        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            if (stroke.Tool == AnnotationTool.Text && GetTextBounds(stroke).Contains(imagePoint))
                return true;
        }

        return false;
    }

    public void ClearSelection()
    {
        if (_selectedText is null)
            return;

        _selectedText = null;
        _movingText = false;
        _resizingText = false;
        _resizeHandle = TextResizeHandle.None;
        InvalidateVisual();
    }

    public void AddStroke(AnnotationStroke stroke)
    {
        _redo.Clear();
        _strokes.Add(stroke);
        InvalidateVisual();
        HistoryChanged?.Invoke();
    }

    public void Undo()
    {
        if (_strokes.Count == 0)
            return;

        var stroke = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        _redo.Add(stroke);

        if (ReferenceEquals(stroke, _selectedText))
        {
            _selectedText = null;
            _movingText = false;
            _resizingText = false;
            _resizeHandle = TextResizeHandle.None;
        }

        InvalidateVisual();
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;

        var stroke = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _strokes.Add(stroke);
        InvalidateVisual();
        HistoryChanged?.Invoke();
    }

    public void Clear()
    {
        _strokes.Clear();
        _redo.Clear();
        _current = null;
        _selectedText = null;
        _drawing = false;
        _movingText = false;
        _resizingText = false;
        _resizeHandle = TextResizeHandle.None;
        InvalidateVisual();
        HistoryChanged?.Invoke();
    }

    public void DrawAnnotations(DrawingContext context, double coordinateDivisor, double thicknessMultiplier)
    {
        foreach (var stroke in _strokes)
            DrawStroke(context, stroke, coordinateDivisor, thicknessMultiplier, 1.0);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        double divisor = Math.Max(0.01, ImageScale);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var stroke in _strokes)
            DrawStroke(drawingContext, stroke, divisor, 1.0, pixelsPerDip);

        if (_current is not null)
            DrawStroke(drawingContext, _current, divisor, 1.0, pixelsPerDip);

        if (_selectedText is not null)
            DrawTextSelection(drawingContext, _selectedText, divisor);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ChangedButton != MouseButton.Left)
            return;

        Focus();
        var point = ToImagePoint(e.GetPosition(this));

        if (Tool == AnnotationTool.Move)
        {
            if (_selectedText is not null)
            {
                var handle = HitTestTextHandle(point, _selectedText);
                if (handle != TextResizeHandle.None)
                {
                    _resizingText = true;
                    _resizeHandle = handle;
                    _resizeStart = point;

                    var size = GetTextLayoutSize(_selectedText);
                    _resizeStartWidth = size.Width;
                    _resizeStartHeight = size.Height;

                    CaptureMouse();
                    UpdateResizeCursor(handle);
                    e.Handled = true;
                    return;
                }
            }

            var text = FindTextAt(point);
            _selectedText = text;
            _movingText = text is not null;

            if (text is not null)
            {
                _moveStart = point;
                _moveOrigin = text.Points[0];
                CaptureMouse();
                Cursor = Cursors.SizeAll;
            }

            InvalidateVisual();
            e.Handled = text is not null;
            return;
        }

        ClearSelection();

        if (Tool == AnnotationTool.Text)
        {
            TextRequested?.Invoke(point);
            e.Handled = true;
            return;
        }

        _current = new AnnotationStroke
        {
            Tool = Tool,
            Color = StrokeColor,
            Thickness = Thickness
        };
        _current.Points.Add(point);
        _rawPoint = point;
        _drawing = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_resizingText && _selectedText is not null)
        {
            var point = ToImagePoint(e.GetPosition(this));
            ResizeText(_selectedText, point);
            e.Handled = true;
            return;
        }

        if (_movingText && _selectedText is not null)
        {
            var point = ToImagePoint(e.GetPosition(this));
            MoveText(_selectedText, point.X - _moveStart.X, point.Y - _moveStart.Y);
            e.Handled = true;
            return;
        }

        if (!_drawing && Tool == AnnotationTool.Move)
        {
            var hover = ToImagePoint(e.GetPosition(this));
            var handle = _selectedText is null
                ? TextResizeHandle.None
                : HitTestTextHandle(hover, _selectedText);

            UpdateResizeCursor(handle);
            return;
        }

        if (!_drawing || _current is null)
            return;

        var canvasPoint = ToImagePoint(e.GetPosition(this));
        var points = _current.Points;

        if (IsTwoPointTool(_current.Tool))
        {
            _rawPoint = canvasPoint;

            var end = ApplyConstraint(_current.Tool, points[0], canvasPoint,
                (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

            if (points.Count < 2)
                points.Add(end);
            else
                points[1] = end;
        }
        else
        {
            var last = points[^1];
            double dx = canvasPoint.X - last.X;
            double dy = canvasPoint.Y - last.Y;
            if (dx * dx + dy * dy >= 1.0)
                points.Add(canvasPoint);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_resizingText)
        {
            _resizingText = false;
            _resizeHandle = TextResizeHandle.None;
            ReleaseMouseCapture();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (_movingText)
        {
            _movingText = false;
            ReleaseMouseCapture();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (!_drawing)
            return;

        _drawing = false;
        ReleaseMouseCapture();

        if (_current is not null)
        {
            if (IsTwoPointTool(_current.Tool))
            {
                var start = _current.Points[0];
                var end = ApplyConstraint(_current.Tool, start, _rawPoint,
                    (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

                if (_current.Points.Count < 2)
                    _current.Points.Add(end);
                else
                    _current.Points[1] = end;
            }
            else if (_current.Points.Count == 1)
            {
                _current.Points.Add(_current.Points[0]);
            }

            _redo.Clear();
            _strokes.Add(_current);
        }

        _current = null;
        InvalidateVisual();
        HistoryChanged?.Invoke();
        e.Handled = true;
    }

    private AnnotationStroke? FindTextAt(Point imagePoint)
    {
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            if (stroke.Tool == AnnotationTool.Text &&
                stroke.Points.Count > 0 &&
                GetTextBounds(stroke).Contains(imagePoint))
            {
                return stroke;
            }
        }

        return null;
    }

    private void MoveText(AnnotationStroke stroke, double dx, double dy)
    {
        if (stroke.Points.Count == 0)
            return;

        double scale = Math.Max(0.01, ImageScale);
        double imageWidth = ActualWidth * scale;
        double imageHeight = ActualHeight * scale;

        var bounds = GetTextBounds(stroke);
        var origin = stroke.Points[0];
        double leftOffset = bounds.Left - origin.X;
        double topOffset = bounds.Top - origin.Y;
        double rightOffset = bounds.Right - origin.X;
        double bottomOffset = bounds.Bottom - origin.Y;

        double minX = -leftOffset;
        double maxX = Math.Max(minX, imageWidth - rightOffset);
        double minY = -topOffset;
        double maxY = Math.Max(minY, imageHeight - bottomOffset);

        stroke.Points[0] = new Point(
            Math.Clamp(_moveOrigin.X + dx, minX, maxX),
            Math.Clamp(_moveOrigin.Y + dy, minY, maxY));

        InvalidateVisual();
    }

    private static bool IsTwoPointTool(AnnotationTool tool) =>
        tool is AnnotationTool.Arrow or AnnotationTool.Line or
            AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Blur;

    /// <summary>
    /// Shift выравнивает линию и стрелку по ближайшим 45°, а овал превращает
    /// в ровный круг. Остальные инструменты Shift не меняет.
    /// </summary>
    private static Point ApplyConstraint(AnnotationTool tool, Point start, Point end, bool shift)
    {
        if (!shift)
            return end;

        double dx = end.X - start.X;
        double dy = end.Y - start.Y;

        if (tool == AnnotationTool.Ellipse)
        {
            double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new Point(
                start.X + (dx < 0 ? -size : size),
                start.Y + (dy < 0 ? -size : size));
        }

        if (tool is AnnotationTool.Line or AnnotationTool.Arrow)
        {
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.001)
                return end;

            const double step = Math.PI / 4;
            double angle = Math.Round(Math.Atan2(dy, dx) / step) * step;

            return new Point(
                start.X + Math.Cos(angle) * length,
                start.Y + Math.Sin(angle) * length);
        }

        return end;
    }

    private Rect GetTextBounds(AnnotationStroke stroke)
    {
        if (stroke.Tool != AnnotationTool.Text ||
            string.IsNullOrWhiteSpace(stroke.Text) ||
            stroke.Points.Count == 0)
        {
            return Rect.Empty;
        }

        var size = GetTextLayoutSize(stroke);
        var origin = stroke.Points[0];
        double padding = 3.0 * Math.Max(0.01, ImageScale);

        return new Rect(
            origin.X - padding,
            origin.Y - padding,
            size.Width + padding * 2,
            size.Height + padding * 2);
    }

    /// <summary>Размер текстового блока в пикселях изображения.</summary>
    private Size GetTextLayoutSize(AnnotationStroke stroke)
    {
        double scale = Math.Max(0.01, ImageScale);
        double fontSizePixels = Math.Max(14.0, stroke.Thickness * 2.2) * scale;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = CreateFormattedText(stroke, fontSizePixels, Brushes.Black, pixelsPerDip);

        if (stroke.TextWidth > 0)
            text.MaxTextWidth = stroke.TextWidth;

        double width = stroke.TextWidth > 0 ? stroke.TextWidth : Math.Max(1, text.Width);
        double height = stroke.TextHeight > 0 ? stroke.TextHeight : Math.Max(1, text.Height);

        return new Size(width, height);
    }

    private TextResizeHandle HitTestTextHandle(Point imagePoint, AnnotationStroke stroke)
    {
        var bounds = GetTextBounds(stroke);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return TextResizeHandle.None;

        double radius = 8.0 * Math.Max(0.01, ImageScale);

        if (Distance(imagePoint, new Point(bounds.Right, bounds.Bottom)) <= radius)
            return TextResizeHandle.Corner;

        if (Distance(imagePoint, new Point(bounds.Right, bounds.Top + bounds.Height / 2)) <= radius)
            return TextResizeHandle.Right;

        if (Distance(imagePoint, new Point(bounds.Left + bounds.Width / 2, bounds.Bottom)) <= radius)
            return TextResizeHandle.Bottom;

        return TextResizeHandle.None;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void ResizeText(AnnotationStroke stroke, Point imagePoint)
    {
        if (stroke.Points.Count == 0)
            return;

        double scale = Math.Max(0.01, ImageScale);
        double imageWidth = ActualWidth * scale;
        double imageHeight = ActualHeight * scale;
        var origin = stroke.Points[0];
        double padding = 3.0 * scale;

        const double minWidthDip = 40;
        double minHeightDip = Math.Max(18, Math.Max(14.0, stroke.Thickness * 2.2) * 0.9);

        double minWidth = minWidthDip * scale;
        double minHeight = minHeightDip * scale;
        double maxWidth = Math.Max(minWidth, imageWidth - origin.X - padding);
        double maxHeight = Math.Max(minHeight, imageHeight - origin.Y - padding);

        double dx = imagePoint.X - _resizeStart.X;
        double dy = imagePoint.Y - _resizeStart.Y;

        if (_resizeHandle is TextResizeHandle.Right or TextResizeHandle.Corner)
            stroke.TextWidth = Math.Clamp(_resizeStartWidth + dx, minWidth, maxWidth);

        if (_resizeHandle is TextResizeHandle.Bottom or TextResizeHandle.Corner)
            stroke.TextHeight = Math.Clamp(_resizeStartHeight + dy, minHeight, maxHeight);

        InvalidateVisual();
    }

    private void UpdateResizeCursor(TextResizeHandle handle)
    {
        Cursor = handle switch
        {
            TextResizeHandle.Right => Cursors.SizeWE,
            TextResizeHandle.Bottom => Cursors.SizeNS,
            TextResizeHandle.Corner => Cursors.SizeNWSE,
            _ => Tool == AnnotationTool.Move ? Cursors.SizeAll : Cursors.Cross
        };
    }

    private void DrawTextSelection(DrawingContext context, AnnotationStroke stroke, double divisor)
    {
        var bounds = GetTextBounds(stroke);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var rect = new Rect(
            bounds.X / divisor,
            bounds.Y / divisor,
            bounds.Width / divisor,
            bounds.Height / divisor);

        context.DrawRectangle(SelectionFill, SelectionPen, rect);

        // Ручки справа, снизу и в правом нижнем углу: тяните, чтобы
        // изменить ширину, высоту или обе сразу.
        DrawSelectionHandle(context, new Point(rect.Right, rect.Top + rect.Height / 2));
        DrawSelectionHandle(context, new Point(rect.Left + rect.Width / 2, rect.Bottom));
        DrawSelectionHandle(context, new Point(rect.Right, rect.Bottom));
    }

    private static void DrawSelectionHandle(DrawingContext context, Point center)
    {
        const double size = 6.0;
        const double half = size / 2;

        context.DrawRectangle(Brushes.White, SelectionHandlePen,
            new Rect(center.X - half, center.Y - half, size, size));
    }

    private static Brush CreateSelectionFill()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0x5A, 0x5F));
        brush.Freeze();
        return brush;
    }

    private static Pen CreateSelectionPen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F));
        brush.Freeze();
        var pen = new Pen(brush, 1.2)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
        };
        pen.Freeze();
        return pen;
    }

    private static Pen CreateSelectionHandlePen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x5F));
        brush.Freeze();
        var pen = new Pen(brush, 1.2);
        pen.Freeze();
        return pen;
    }

    private Point ToImagePoint(Point canvasPoint)
    {
        double scale = Math.Max(0.01, ImageScale);
        double maxX = ActualWidth * scale;
        double maxY = ActualHeight * scale;
        return new Point(
            Math.Clamp(canvasPoint.X * scale, 0, maxX),
            Math.Clamp(canvasPoint.Y * scale, 0, maxY));
    }

    private void DrawStroke(DrawingContext context, AnnotationStroke stroke,
        double coordinateDivisor, double thicknessMultiplier, double pixelsPerDip)
    {
        if (stroke.Points.Count == 0)
            return;

        Color color = stroke.Color;
        if (stroke.Tool == AnnotationTool.Highlighter)
            color = Color.FromArgb(0x66, color.R, color.G, color.B);

        var brush = new SolidColorBrush(color);
        brush.Freeze();

        double thickness = Math.Max(1.0, stroke.Thickness * thicknessMultiplier);
        if (stroke.Tool == AnnotationTool.Highlighter)
            thickness *= 2.0;

        switch (stroke.Tool)
        {
            case AnnotationTool.Arrow:
                DrawArrow(context, stroke, brush, coordinateDivisor, thickness);
                return;

            case AnnotationTool.Line:
                DrawLine(context, stroke, brush, coordinateDivisor, thickness);
                return;

            case AnnotationTool.Rectangle:
                DrawRectangle(context, stroke, brush, coordinateDivisor, thickness);
                return;

            case AnnotationTool.Ellipse:
                DrawEllipse(context, stroke, brush, coordinateDivisor, thickness);
                return;

            case AnnotationTool.Blur:
                DrawBlur(context, stroke, coordinateDivisor);
                return;

            case AnnotationTool.Text:
                DrawText(context, stroke, brush, coordinateDivisor, thickness, pixelsPerDip, thicknessMultiplier);
                return;
        }

        if (stroke.Points.Count == 1 || thickness <= 0)
        {
            var dot = Scale(stroke.Points[0], coordinateDivisor);
            context.DrawEllipse(brush, null, dot, thickness / 2, thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var geometryContext = geometry.Open())
        {
            geometryContext.BeginFigure(Scale(stroke.Points[0], coordinateDivisor), isFilled: false, isClosed: false);
            var rest = new List<Point>(stroke.Points.Count - 1);
            for (int i = 1; i < stroke.Points.Count; i++)
                rest.Add(Scale(stroke.Points[i], coordinateDivisor));

            geometryContext.PolyLineTo(rest, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawRectangle(DrawingContext context, AnnotationStroke stroke, Brush brush,
        double coordinateDivisor, double thickness)
    {
        Rect rect = GetRect(stroke, coordinateDivisor);
        if (rect.Width < 0.5 && rect.Height < 0.5)
            return;

        var pen = CreatePen(brush, thickness);
        context.DrawRectangle(null, pen, rect);
    }

    private static void DrawEllipse(DrawingContext context, AnnotationStroke stroke, Brush brush,
        double coordinateDivisor, double thickness)
    {
        Rect rect = GetRect(stroke, coordinateDivisor);
        if (rect.Width < 0.5 && rect.Height < 0.5)
            return;

        var pen = CreatePen(brush, thickness);
        var center = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        context.DrawEllipse(null, pen, center, rect.Width / 2, rect.Height / 2);
    }

    private static void DrawText(DrawingContext context, AnnotationStroke stroke, Brush brush,
        double coordinateDivisor, double thickness, double pixelsPerDip, double thicknessMultiplier)
    {
        if (string.IsNullOrWhiteSpace(stroke.Text))
            return;

        // Толщина для текста — размер шрифта. На экране это DIP, при экспорте
        // толщина уже умножена на масштаб, поэтому размер остаётся тем же.
        double fontSize = Math.Max(14.0 * thicknessMultiplier, thickness * 2.2);
        var text = CreateFormattedText(stroke, fontSize, brush, pixelsPerDip);

        double maxWidth = stroke.TextWidth > 0
            ? stroke.TextWidth / coordinateDivisor
            : double.PositiveInfinity;
        double maxHeight = stroke.TextHeight > 0
            ? stroke.TextHeight / coordinateDivisor
            : double.PositiveInfinity;

        if (!double.IsInfinity(maxWidth))
            text.MaxTextWidth = maxWidth;

        if (!double.IsInfinity(maxHeight))
            text.MaxTextHeight = maxHeight;

        var origin = Scale(stroke.Points[0], coordinateDivisor);

        if (double.IsInfinity(maxWidth) && double.IsInfinity(maxHeight))
        {
            context.DrawText(text, origin);
            return;
        }

        // Если пользователь задал размеры текстового блока, лишнее
        // обрезаем — ровно как в поле ввода.
        double clipWidth = double.IsInfinity(maxWidth) ? text.Width : maxWidth;
        double clipHeight = double.IsInfinity(maxHeight) ? text.Height : maxHeight;

        context.PushClip(new RectangleGeometry(
            new Rect(origin.X, origin.Y, Math.Max(1, clipWidth), Math.Max(1, clipHeight))));
        context.DrawText(text, origin);
        context.Pop();
    }

    private static FormattedText CreateFormattedText(AnnotationStroke stroke, double fontSize,
        Brush brush, double pixelsPerDip) => new(
        stroke.Text ?? string.Empty,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        TextTypeface,
        fontSize,
        brush,
        pixelsPerDip);

    private void DrawBlur(DrawingContext context, AnnotationStroke stroke, double coordinateDivisor)
    {
        if (stroke.Points.Count < 2)
            return;

        Rect physicalRect = GetRect(stroke, 1.0);
        int left = (int)Math.Floor(physicalRect.Left);
        int top = (int)Math.Floor(physicalRect.Top);
        int right = (int)Math.Ceiling(physicalRect.Right);
        int bottom = (int)Math.Ceiling(physicalRect.Bottom);

        EnsureSourcePixels();

        if (_sourcePixels is null)
        {
            var fallback = new Rect(
                left / coordinateDivisor,
                top / coordinateDivisor,
                Math.Max(1, right - left) / coordinateDivisor,
                Math.Max(1, bottom - top) / coordinateDivisor);
            var dark = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));
            dark.Freeze();
            context.DrawRectangle(dark, null, fallback);
            return;
        }

        left = Math.Clamp(left, 0, _sourceWidth);
        top = Math.Clamp(top, 0, _sourceHeight);
        right = Math.Clamp(right, 0, _sourceWidth);
        bottom = Math.Clamp(bottom, 0, _sourceHeight);

        if (right <= left || bottom <= top)
            return;

        // Размер «пикселя» мозаики один и тот же на экране и в экспорте.
        int block = Math.Max(6, (int)Math.Round(stroke.Thickness * 2.0 * Math.Max(0.01, ImageScale)));

        for (int y = top; y < bottom; y += block)
        {
            int cellBottom = Math.Min(bottom, y + block);
            for (int x = left; x < right; x += block)
            {
                int cellRight = Math.Min(right, x + block);
                var color = AverageColor(x, y, cellRight - x, cellBottom - y);
                var brush = new SolidColorBrush(color);
                brush.Freeze();

                context.DrawRectangle(brush, null, new Rect(
                    x / coordinateDivisor,
                    y / coordinateDivisor,
                    (cellRight - x) / coordinateDivisor,
                    (cellBottom - y) / coordinateDivisor));
            }
        }
    }

    private Color AverageColor(int x, int y, int width, int height)
    {
        if (_sourcePixels is null || width <= 0 || height <= 0)
            return Color.FromRgb(0x12, 0x14, 0x18);

        int stepX = Math.Max(1, width / 8);
        int stepY = Math.Max(1, height / 8);
        long blue = 0;
        long green = 0;
        long red = 0;
        int count = 0;

        int maxY = y + height;
        int maxX = x + width;
        for (int yy = y; yy < maxY; yy += stepY)
        {
            int row = yy * _sourceStride;
            for (int xx = x; xx < maxX; xx += stepX)
            {
                int index = row + xx * 4;
                blue += _sourcePixels[index];
                green += _sourcePixels[index + 1];
                red += _sourcePixels[index + 2];
                count++;
            }
        }

        if (count == 0)
            return Color.FromRgb(0x12, 0x14, 0x18);

        return Color.FromRgb(
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count));
    }

    private void EnsureSourcePixels()
    {
        if (_sourcePixels is not null || _sourceImage is null)
            return;

        try
        {
            BitmapSource source = _sourceImage;
            if (source.Format != PixelFormats.Bgra32)
                source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            _sourceWidth = source.PixelWidth;
            _sourceHeight = source.PixelHeight;
            _sourceStride = _sourceWidth * 4;
            _sourcePixels = new byte[_sourceStride * _sourceHeight];
            source.CopyPixels(_sourcePixels, _sourceStride, 0);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось подготовить пиксели для размытия", ex);
            _sourcePixels = null;
        }
    }

    private static Rect GetRect(AnnotationStroke stroke, double coordinateDivisor)
    {
        Point start = Scale(stroke.Points[0], coordinateDivisor);
        Point end = Scale(stroke.Points[^1], coordinateDivisor);
        return new Rect(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawLine(DrawingContext context, AnnotationStroke stroke, Brush brush,
        double coordinateDivisor, double thickness)
    {
        Point start = Scale(stroke.Points[0], coordinateDivisor);
        Point end = Scale(stroke.Points[^1], coordinateDivisor);

        if ((end - start).Length < 1.0)
        {
            context.DrawEllipse(brush, null, end, thickness / 2, thickness / 2);
            return;
        }

        var pen = CreatePen(brush, thickness);
        context.DrawLine(pen, start, end);
    }

    private static void DrawArrow(DrawingContext context, AnnotationStroke stroke, Brush brush,
        double coordinateDivisor, double thickness)
    {
        Point start = Scale(stroke.Points[0], coordinateDivisor);
        Point end = Scale(stroke.Points[^1], coordinateDivisor);

        var direction = end - start;
        if (direction.Length < 1.0)
        {
            context.DrawEllipse(brush, null, end, thickness / 2, thickness / 2);
            return;
        }

        direction.Normalize();
        double headLength = Math.Max(11.0, thickness * 3.4);
        double headWidth = Math.Max(9.0, thickness * 2.2);
        var normal = new Vector(-direction.Y, direction.X);

        Point tip = end;
        Point baseCenter = tip - direction * headLength;
        Point left = baseCenter + normal * (headWidth / 2);
        Point right = baseCenter - normal * (headWidth / 2);

        var pen = CreatePen(brush, thickness);
        context.DrawLine(pen, start, baseCenter - direction * 1.0);

        var head = new StreamGeometry();
        using (var geometryContext = head.Open())
        {
            geometryContext.BeginFigure(tip, isFilled: true, isClosed: true);
            geometryContext.LineTo(left, isStroked: true, isSmoothJoin: false);
            geometryContext.LineTo(right, isStroked: true, isSmoothJoin: false);
        }

        head.Freeze();
        context.DrawGeometry(brush, null, head);
    }

    private static Point Scale(Point point, double divisor) =>
        new(point.X / divisor, point.Y / divisor);
}
