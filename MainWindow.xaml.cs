using System.ComponentModel;
using System.Windows.Controls.Primitives;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip;

public partial class MainWindow : Window
{
    private readonly App _app;
    private bool _trayHintShown;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        Loaded += (_, _) =>
        {
            Log.Info($"Главное окно: {ActualWidth:0}×{ActualHeight:0} DIP, DPI {VisualTreeHelper.GetDpi(this).PixelsPerDip:0.##}, позиция {Left:0},{Top:0}");
            RefreshFromSettings();
            RefreshMonitors();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshFromSettings();
                RefreshMonitors();
            }
        };
        Closing += MainWindow_Closing;
    }

    public void RefreshFromSettings()
    {
        HotkeyHint.Text = _app.Settings.Hotkey.DisplayName;
        FullMonitorHotkeyHint.Text = _app.Settings.FullMonitorHotkey.DisplayName;
        SaveHint.Text = _app.Settings.SaveToFile
            ? "Автосохранение: " + _app.Settings.SaveFolder
            : "Автосохранение выключено";
        SaveHint.ToolTip = _app.Settings.SaveFolder;
    }

    private void RefreshMonitors()
    {
        MonitorsPanel.Children.Clear();

        List<MonitorInfo> monitors;
        try
        {
            monitors = DisplayService.GetMonitors();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось обновить список мониторов", ex);
            return;
        }

        HdrSummary.Text = monitors.Any(m => m.IsHdr) ? "HDR обнаружен" : "HDR не обнаружен";

        Brush muted = (Brush)FindResource("MutedBrush");
        Brush green = (Brush)FindResource("GreenBrush");

        foreach (var monitor in monitors)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Background = monitor.IsHdr
                    ? green
                    : new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x68)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(dot, 0);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(monitor.Name) ? "Монитор" : monitor.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            texts.Children.Add(new TextBlock
            {
                Text = monitor.ResolutionText +
                       (monitor.IsHdr
                           ? $" · HDR10, SDR-white {(int)Math.Round(monitor.SdrWhiteNits)} нит"
                           : " · SDR"),
                FontSize = 11,
                Foreground = muted,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(texts, 1);

            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Background = monitor.IsHdr
                    ? new SolidColorBrush(Color.FromRgb(0x16, 0x33, 0x28))
                    : new SolidColorBrush(Color.FromRgb(0x22, 0x28, 0x33)),
                Child = new TextBlock
                {
                    Text = monitor.IsHdr ? "HDR" : "SDR",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = monitor.IsHdr ? green : muted
                }
            };
            Grid.SetColumn(badge, 2);

            grid.Children.Add(dot);
            grid.Children.Add(texts);
            grid.Children.Add(badge);

            MonitorsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x16, 0x1E)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 7),
                Child = grid
            });
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove может не сработать при быстром отпускании кнопки.
        }
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase)
                return true;

            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Capture_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Controller.CaptureAsync();

    private void CaptureFullMonitor_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Controller.CaptureFullMonitorAsync();

    private void CaptureWindow_Click(object sender, RoutedEventArgs e) =>
        _ = _app.Controller.CaptureWindowAsync();

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        _app.Controller.OpenSettings();

    private void Folder_Click(object sender, RoutedEventArgs e) =>
        _app.Controller.OpenScreenshotsFolder();

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) =>
        HideToTray();

    private void Exit_Click(object sender, RoutedEventArgs e) =>
        _app.RequestExit();

    private void HideToTray()
    {
        Hide();

        if (_trayHintShown)
            return;

        _trayHintShown = true;
        _app.Tray?.Notify("HDR Snip", "Приложение свёрнуто в трей. Двойной клик по иконке — новый скриншот.");
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_app.IsExiting)
            return;

        e.Cancel = true;
        HideToTray();
    }
}
