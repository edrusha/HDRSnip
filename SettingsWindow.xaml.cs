using System.Windows.Controls.Primitives;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly AppSettings _original;
    private HotkeyBinding _binding;
    private HotkeyBinding _fullMonitorBinding;
    private HotkeyTarget _recording;

    private enum HotkeyTarget
    {
        None,
        Region,
        FullMonitor
    }

    public SettingsWindow(App app)
    {
        _app = app;
        _original = app.Settings.Clone();
        _binding = app.Settings.Hotkey;
        _fullMonitorBinding = app.Settings.FullMonitorHotkey;

        InitializeComponent();

        SourceInitialized += (_, _) => WindowAppearance.Apply(this);
        PreviewKeyDown += Window_PreviewKeyDown;
        Deactivated += (_, _) => StopRecording();

        SaveToFileBox.IsChecked = _original.SaveToFile;
        FolderBox.Text = string.IsNullOrWhiteSpace(_original.SaveFolder)
            ? AppSettings.DefaultSaveFolder
            : _original.SaveFolder;
        FolderBox.ToolTip = FolderBox.Text;
        ToolTipService.SetShowDuration(FolderBox, 30000);
        FolderBox.TextChanged += (_, _) => FolderBox.ToolTip = FolderBox.Text;
        ShowToolsBox.IsChecked = _original.ShowAnnotationTools;
        ShowCursorBox.IsChecked = _original.ShowCursor;
        FullMonitorBox.IsChecked = _original.CaptureFullMonitor;
        NotificationsBox.IsChecked = _original.ShowNotifications;
        StartupBox.IsChecked = _original.StartWithWindows;
        StartMinimizedBox.IsChecked = _original.StartMinimized;

        UpdateHotkeyTexts();
        UpdateFolderState();
    }

    private void SaveToFile_Changed(object sender, RoutedEventArgs e) => UpdateFolderState();

    private void UpdateFolderState()
    {
        // Папка должна быть видна всегда: путь не приглушаем и не блокируем,
        // даже если автосохранение сейчас выключено. Кнопка «Обзор…» тоже
        // остаётся доступной, чтобы папку можно было выбрать заранее.
        FolderBox.IsReadOnly = false;
        FolderBox.Opacity = 1;
        BrowseButton.IsEnabled = true;
        BrowseButton.Opacity = 1;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Папка для сохранения скриншотов",
                Multiselect = false
            };

            var current = FolderBox.Text.Trim();
            if (Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(this) == true)
                FolderBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Не удалось открыть выбор папки: " + ex.Message;
        }
    }

    private void HotkeyField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartRecording(HotkeyTarget.Region);
        e.Handled = true;
    }

    private void FullMonitorHotkeyField_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartRecording(HotkeyTarget.FullMonitor);
        e.Handled = true;
    }

    private void StartRecording(HotkeyTarget target)
    {
        _recording = target;
        ErrorText.Text = "";
        ResetHotkeyFields();

        if (target == HotkeyTarget.Region)
        {
            HotkeyText.Text = "Нажмите сочетание…";
            HotkeyField.BorderBrush = (Brush)FindResource("AccentBrush");
            HotkeyField.Focus();
            Keyboard.Focus(HotkeyField);
        }
        else
        {
            FullMonitorHotkeyText.Text = "Нажмите сочетание…";
            FullMonitorHotkeyField.BorderBrush = (Brush)FindResource("AccentBrush");
            FullMonitorHotkeyField.Focus();
            Keyboard.Focus(FullMonitorHotkeyField);
        }
    }

    private void StopRecording()
    {
        if (_recording == HotkeyTarget.None)
            return;

        _recording = HotkeyTarget.None;
        ResetHotkeyFields();
        UpdateHotkeyTexts();
    }

    private void ResetHotkeyFields()
    {
        var stroke = (Brush)FindResource("StrokeBrush");
        var text = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("MutedBrush");

        HotkeyField.BorderBrush = stroke;
        FullMonitorHotkeyField.BorderBrush = stroke;
        HotkeyText.Foreground = text;
        FullMonitorHotkeyText.Foreground = text;
        HotkeyIcon.Foreground = muted;
        FullMonitorHotkeyIcon.Foreground = muted;
    }

    private void UpdateHotkeyTexts()
    {
        HotkeyText.Text = _binding.DisplayName;
        FullMonitorHotkeyText.Text = _fullMonitorBinding.DisplayName;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording == HotkeyTarget.None)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }

            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            StopRecording();
            return;
        }

        if (key == Key.None || IsModifierKey(key) ||
            key is Key.ImeProcessed or Key.DeadCharProcessed or Key.Apps)
        {
            SetRecordingMessage("… + клавиша");
            return;
        }

        var binding = HotkeyBinding.FromKeyboard(Keyboard.Modifiers, key);
        if (!binding.IsValid)
        {
            SetRecordingMessage("Нужен модификатор или F-клавиша", accent: true);
            return;
        }

        var other = _recording == HotkeyTarget.Region ? _fullMonitorBinding : _binding;
        if (binding.IsSameAs(other))
        {
            SetRecordingMessage("Это сочетание уже используется", accent: true);
            return;
        }

        if (_recording == HotkeyTarget.Region)
            _binding = binding;
        else
            _fullMonitorBinding = binding;

        _recording = HotkeyTarget.None;
        ResetHotkeyFields();
        UpdateHotkeyTexts();
    }

    private void SetRecordingMessage(string message, bool accent = false)
    {
        var target = _recording == HotkeyTarget.Region ? HotkeyText : FullMonitorHotkeyText;
        target.Text = message;
        target.Foreground = (Brush)FindResource(accent ? "AccentBrush" : "TextBrush");
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        if (_binding.IsSameAs(_fullMonitorBinding))
        {
            ErrorText.Text = "Для выделения области и для всего монитора нужно выбрать разные сочетания.";
            return;
        }

        bool saveToFile = SaveToFileBox.IsChecked == true;
        string folder = FolderBox.Text.Trim();

        if (saveToFile)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                ErrorText.Text = "Укажите папку для сохранения скриншотов.";
                return;
            }

            try
            {
                folder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder));
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                ErrorText.Text = "Не удалось использовать эту папку: " + ex.Message;
                return;
            }
        }
        else if (string.IsNullOrWhiteSpace(folder))
        {
            folder = AppSettings.DefaultSaveFolder;
        }

        var settings = _original.Clone();
        settings.SaveToFile = saveToFile;
        settings.SaveFolder = folder;
        settings.ShowAnnotationTools = ShowToolsBox.IsChecked == true;
        settings.ShowCursor = ShowCursorBox.IsChecked == true;
        settings.CaptureFullMonitor = FullMonitorBox.IsChecked == true;
        settings.ShowNotifications = NotificationsBox.IsChecked == true;
        settings.StartWithWindows = StartupBox.IsChecked == true;
        settings.StartMinimized = StartMinimizedBox.IsChecked == true;
        settings.HotkeyModifiers = _binding.Modifiers;
        settings.HotkeyKey = _binding.VirtualKey;
        settings.FullMonitorHotkeyModifiers = _fullMonitorBinding.Modifiers;
        settings.FullMonitorHotkeyKey = _fullMonitorBinding.VirtualKey;

        if (!_app.Controller.TryApplySettings(settings, out var error))
        {
            ErrorText.Text = string.IsNullOrWhiteSpace(error)
                ? "Не удалось применить настройки."
                : error;
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

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
            // ignore
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
}
