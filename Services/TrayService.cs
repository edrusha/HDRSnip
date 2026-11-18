using System.Windows.Forms;
using HDRSnip.Models;
using HDRSnip.UI;
using Color = System.Drawing.Color;
using Font = System.Drawing.Font;
using Icon = System.Drawing.Icon;
using SystemIcons = System.Drawing.SystemIcons;

namespace HDRSnip.Services;

public sealed class TrayService : IDisposable
{
    private static readonly Color MenuBackground = Color.FromArgb(23, 28, 36);
    private static readonly Color MenuText = Color.FromArgb(242, 245, 250);

    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _singleClickTimer;
    private readonly Func<bool> _notificationsEnabled;
    private readonly ToolStripMenuItem _captureItem;
    private readonly ToolStripMenuItem _fullMonitorItem;
    private Icon? _icon;
    private ToastWindow? _toast;
    private DateTime _suppressLeftClickUntil;
    private bool _disposed;

    public TrayService(Func<bool> notificationsEnabled)
    {
        _notificationsEnabled = notificationsEnabled;

        var menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = MenuBackground,
            ForeColor = MenuText,
            Font = new Font("Segoe UI", 9.5f),
            Renderer = new DarkTrayRenderer()
        };

        var showItem = CreateItem("Открыть окно");
        showItem.Click += (_, _) => ShowRequested?.Invoke();

        _captureItem = CreateItem("Сделать скриншот");
        _captureItem.Click += (_, _) => CaptureRequested?.Invoke();

        _fullMonitorItem = CreateItem("Скриншот всего монитора");
        _fullMonitorItem.Click += (_, _) => FullMonitorRequested?.Invoke();

        var windowItem = CreateItem("Снимок окна");
        windowItem.Click += (_, _) => WindowCaptureRequested?.Invoke();

        var settingsItem = CreateItem("Настройки…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var folderItem = CreateItem("Открыть папку со скриншотами");
        folderItem.Click += (_, _) => OpenFolderRequested?.Invoke();

        var exitItem = CreateItem("Выход");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_captureItem);
        menu.Items.Add(_fullMonitorItem);
        menu.Items.Add(windowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(folderItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _icon = LoadIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "HDR Snip — скриншоты с поддержкой HDR",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Одиночный левый клик открывает окно, но с небольшой задержкой:
        // так он не срабатывает перед двойным кликом, который делает снимок.
        _singleClickTimer = new System.Windows.Forms.Timer
        {
            Interval = 300
        };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            ShowRequested?.Invoke();
        };

        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                _singleClickTimer.Stop();

                // Меню показывает сам NotifyIcon через ContextMenuStrip:
                // так оно ведёт себя как обычное меню трея — не создаёт
                // лишних окон и закрывается по клику мимо.
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (DateTime.UtcNow < _suppressLeftClickUntil)
                {
                    // Это второй клик двойного клика — окно не открываем.
                    _singleClickTimer.Stop();
                    return;
                }

                if (_singleClickTimer.Enabled)
                {
                    // Второй клик — это уже двойной клик, окно не открываем.
                    _singleClickTimer.Stop();
                }
                else
                {
                    _singleClickTimer.Start();
                }
            }
        };

        _notifyIcon.DoubleClick += (_, _) =>
        {
            _singleClickTimer.Stop();
            _suppressLeftClickUntil = DateTime.UtcNow.AddMilliseconds(SystemInformation.DoubleClickTime);
            CaptureRequested?.Invoke();
        };
    }

    public event Action? CaptureRequested;
    public event Action? FullMonitorRequested;
    public event Action? WindowCaptureRequested;
    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? OpenFolderRequested;
    public event Action? ExitRequested;

    private static ToolStripMenuItem CreateItem(string text) => new(text)
    {
        ForeColor = MenuText,
        BackColor = MenuBackground,
        Padding = new Padding(2, 3, 2, 3)
    };

    public void UpdateHotkeys(HotkeyBinding region, HotkeyBinding fullMonitor)
    {
        _captureItem.ShortcutKeyDisplayString = region.DisplayName;
        _fullMonitorItem.ShortcutKeyDisplayString = fullMonitor.DisplayName;
    }

    public void Notify(string title, string message)
    {
        if (_disposed || !_notificationsEnabled())
            return;

        ShowToast(title, message, error: false);
    }

    public void NotifyError(string message)
    {
        if (_disposed)
            return;

        ShowToast("HDR Snip", message, error: true);
    }

    private void ShowToast(string title, string message, bool error)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app is null)
                return;

            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(new Action(() => ShowToast(title, message, error)));
                return;
            }

            _toast ??= new ToastWindow();
            _toast.ShowMessage(title, message, error);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось показать уведомление: " + ex.Message);
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Resources/app.ico"));
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                return new Icon(stream, 32, 32);
            }

            var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(iconPath))
                return new Icon(iconPath, 32, 32);

            var process = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(process))
            {
                var extracted = Icon.ExtractAssociatedIcon(process);
                if (extracted is not null)
                    return extracted;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось загрузить иконку: " + ex.Message);
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _toast?.CloseToast();
            _toast = null;
        }
        catch
        {
            // ignore
        }

        try
        {
            _singleClickTimer.Stop();
            _singleClickTimer.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _icon?.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
