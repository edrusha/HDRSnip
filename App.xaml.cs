using System.Windows.Interop;
using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.Services;
using HDRSnip.UI;

namespace HDRSnip;

public partial class App : Application
{
    private Mutex? _mutex;
    private HwndSource? _messageSource;
    private uint _showMessage;
    private bool _disposed;

    public AppSettings Settings { get; private set; } = new();
    public MainWindow? MainUi { get; private set; }
    public TrayService? Tray { get; private set; }
    public HotkeyService? Hotkey { get; private set; }
    public CaptureController Controller { get; private set; } = null!;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _showMessage = Native.RegisterWindowMessage("HDRSnip.ShowMainWindow");

        _mutex = new Mutex(initiallyOwned: true, @"Local\HDRSnip.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Native.PostMessage(Native.HwndBroadcast, _showMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Необработанная ошибка", args.Exception);
            args.Handled = true;

            try
            {
                System.Windows.MessageBox.Show(
                    "Произошла непредвиденная ошибка. Подробности записаны в журнал.\n\n" +
                    args.Exception.Message,
                    "HDR Snip",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
        };

        Settings = SettingsService.Load();
        StartupService.Apply(Settings.StartWithWindows);

        CreateMessageWindow();

        Tray = new TrayService(() => Settings.ShowNotifications);
        Tray.CaptureRequested += () => _ = Controller.CaptureAsync();
        Tray.FullMonitorRequested += () => _ = Controller.CaptureFullMonitorAsync();
        Tray.WindowCaptureRequested += () => _ = Controller.CaptureWindowAsync();
        Tray.ShowRequested += ShowMainWindow;
        Tray.SettingsRequested += () => Controller.OpenSettings();
        Tray.OpenFolderRequested += () => Controller.OpenScreenshotsFolder();
        Tray.ExitRequested += RequestExit;
        Tray.UpdateHotkeys(Settings.Hotkey, Settings.FullMonitorHotkey);

        Controller = new CaptureController(this);

        Hotkey = new HotkeyService(_messageSource!.Handle);
        Hotkey.Pressed += () => _ = Controller.CaptureAsync();
        Hotkey.FullMonitorPressed += () => _ = Controller.CaptureFullMonitorAsync();
        Hotkey.Apply(Settings.Hotkey, Settings.FullMonitorHotkey);

        MainUi = new MainWindow(this);

        bool startHidden = Settings.StartMinimized ||
            Settings.StartWithWindows ||
            e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));

        if (!startHidden)
            MainUi.Show();

        Log.Info("HDR Snip запущен" + (startHidden ? " (в трее)" : ""));
    }

    private void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("HDRSnip.MessageWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW
            Width = 0,
            Height = 0
        };

        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == Native.WmHotkey)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyService.RegionHotkeyId)
            {
                handled = true;
                Hotkey?.Raise();
                return IntPtr.Zero;
            }

            if (id == HotkeyService.FullMonitorHotkeyId)
            {
                handled = true;
                Hotkey?.RaiseFullMonitor();
                return IntPtr.Zero;
            }
        }

        if (_showMessage != 0 && message == _showMessage)
        {
            handled = true;
            ShowMainWindow();
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    public void ShowMainWindow()
    {
        if (MainUi is not null)
            ShowWindow(MainUi);
    }

    public void ShowWindow(Window window)
    {
        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Focus();

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            Native.SetForegroundWindow(handle);
    }

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        SettingsService.Save(settings);
        StartupService.Apply(settings.StartWithWindows);
        Tray?.UpdateHotkeys(settings.Hotkey, settings.FullMonitorHotkey);
        MainUi?.RefreshFromSettings();
    }

    public void RequestExit()
    {
        if (IsExiting)
            return;

        IsExiting = true;
        Hotkey?.Dispose();
        Tray?.Dispose();
        _messageSource?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_disposed)
        {
            _disposed = true;
            Hotkey?.Dispose();
            Tray?.Dispose();
            _messageSource?.Dispose();
            _mutex?.Dispose();
        }

        base.OnExit(e);
    }
}
