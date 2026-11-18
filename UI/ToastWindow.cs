using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HDRSnip.Interop;
using HDRSnip.Services;

namespace HDRSnip.UI;

/// <summary>
/// Собственная всплывающая карточка вместо системного balloon tip.
/// Windows может отключать уведомления трея (режим «Не беспокоить»,
/// выключенные баннеры), поэтому рисуем уведомление сами.
/// </summary>
internal sealed class ToastWindow : Window
{
    private const double ToastWidthDip = 430;
    private const double EdgeMarginDip = 16;

    private static readonly Brush SuccessColor = Frozen(Color.FromRgb(0x3D, 0xDC, 0x97));
    private static readonly Brush SuccessBadgeColor = Frozen(Color.FromRgb(0x17, 0x35, 0x2A));
    private static readonly Brush ErrorColor = Frozen(Color.FromRgb(0xFF, 0x72, 0x76));
    private static readonly Brush ErrorBadgeColor = Frozen(Color.FromRgb(0x3A, 0x20, 0x24));
    private static readonly Brush TitleColor = Frozen(Color.FromRgb(0xF2, 0xF5, 0xFA));
    private static readonly Brush MessageColor = Frozen(Color.FromRgb(0xC4, 0xCE, 0xDD));

    private readonly Border _iconBadge;
    private readonly TextBlock _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _message;
    private readonly DispatcherTimer _timer;
    private int _generation;

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = ToastWidthDip;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _icon = new TextBlock
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _iconBadge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            VerticalAlignment = VerticalAlignment.Top,
            Child = _icon
        };

        _title = new TextBlock
        {
            Text = "HDR Snip",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleColor
        };

        _message = new TextBlock
        {
            FontSize = 12.5,
            Foreground = MessageColor,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 56,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var texts = new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        texts.Children.Add(_title);
        texts.Children.Add(_message);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_iconBadge, 0);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(_iconBadge);
        grid.Children.Add(texts);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1C, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x41)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14),
            Child = grid
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        _timer.Tick += (_, _) => BeginHide();

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            // Не активируем окно и не перехватываем мышь: уведомление не должно
            // мешать редактору и кликам по рабочему столу.
            int style = Native.GetWindowLong(handle, Native.GwlExStyle);
            Native.SetWindowLong(handle, Native.GwlExStyle,
                style | Native.WsExNoActivate | Native.WsExTransparent | Native.WsExToolWindow);
        };

        Loaded += (_, _) => Position();
        SizeChanged += (_, _) => Position();
    }

    public void ShowMessage(string title, string message, bool error)
    {
        _generation++;
        _timer.Stop();

        _title.Text = string.IsNullOrWhiteSpace(title) ? "HDR Snip" : title;
        _message.Text = Shorten(message, 260);
        _icon.Text = error ? "\uE783" : "\uE73E";
        _icon.Foreground = error ? ErrorColor : SuccessColor;
        _iconBadge.Background = error ? ErrorBadgeColor : SuccessBadgeColor;

        Opacity = 0;
        if (!IsVisible)
            Show();

        UpdateLayout();
        Position();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        _timer.Start();
    }

    public void CloseToast()
    {
        _generation++;
        _timer.Stop();

        try
        {
            Close();
        }
        catch
        {
            // Окно уже могло быть закрыто.
        }
    }

    private void BeginHide()
    {
        _timer.Stop();
        int generation = _generation;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) =>
        {
            if (generation != _generation)
                return;

            try
            {
                Hide();
            }
            catch
            {
                // ignore
            }
        };

        BeginAnimation(OpacityProperty, fade);
    }

    private void Position()
    {
        if (!IsVisible || ActualHeight <= 0)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var monitors = DisplayService.GetMonitors(writeLog: false);
        var monitor = DisplayService.MonitorUnderCursor(monitors);
        if (monitor is null)
            return;

        double scale = Math.Max(0.5, monitor.Scale);
        int width = (int)Math.Round(ToastWidthDip * scale);
        int height = (int)Math.Round(ActualHeight * scale);
        int margin = (int)Math.Round(EdgeMarginDip * scale);

        int x = monitor.Work.Right - width - margin;
        int y = monitor.Work.Bottom - height - margin;

        Native.SetWindowPos(handle, Native.HwndTopmost, x, y, width, height, Native.SwpNoActivate);
    }

    private static string Shorten(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return text.Length <= maxLength
            ? text
            : text[..(maxLength - 1)] + "…";
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
