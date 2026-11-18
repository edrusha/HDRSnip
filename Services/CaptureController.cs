using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.UI;

namespace HDRSnip.Services;

public sealed class CaptureController
{
    private readonly App _app;
    private bool _busy;
    private bool _settingsOpen;
    private SettingsWindow? _settingsWindow;

    public CaptureController(App app)
    {
        _app = app;
    }

    public bool IsBusy => _busy;

    public Task CaptureAsync() => CaptureCoreAsync(forceFullMonitor: false);

    public Task CaptureFullMonitorAsync() => CaptureCoreAsync(forceFullMonitor: true);

    public Task CaptureWindowAsync() => CaptureWindowCoreAsync();

    private async Task CaptureWindowCoreAsync()
    {
        if (_busy || _settingsOpen)
            return;

        _busy = true;
        bool mainWasVisible = _app.MainUi?.IsVisible == true;

        try
        {
            if (mainWasVisible)
                _app.MainUi!.Hide();

            await Task.Delay(180);

            var monitors = DisplayService.GetMonitors();
            if (monitors.Count == 0)
            {
                NotifyError("Мониторы не найдены.");
                return;
            }

            var shots = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in monitors)
            {
                try
                {
                    shots[monitor.DeviceName] = CaptureService.CaptureMonitor(monitor);
                }
                catch (Exception ex)
                {
                    Log.Error($"Не удалось захватить {monitor.Name}", ex);
                }
            }

            if (shots.Count == 0)
            {
                NotifyError("Не удалось захватить экран.");
                return;
            }

            var selection = await SelectWindowAsync(monitors, shots);
            if (selection is null)
                return;

            var (selectedMonitor, rect) = selection.Value;
            if (!shots.TryGetValue(selectedMonitor.DeviceName, out var source))
                return;

            var screenRect = new Native.Rect
            {
                Left = selectedMonitor.Bounds.Left + rect.X,
                Top = selectedMonitor.Bounds.Top + rect.Y,
                Right = selectedMonitor.Bounds.Left + rect.X + rect.Width,
                Bottom = selectedMonitor.Bounds.Top + rect.Y + rect.Height
            };

            var cropped = CaptureService.Crop(source, rect);
            await ProcessImageAsync(cropped, selectedMonitor, screenRect, source);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка захвата окна", ex);
            NotifyError("Не удалось снять окно: " + ex.Message);
        }
        finally
        {
            _busy = false;

            if (mainWasVisible && !_app.IsExiting && _app.MainUi is not null && !_app.MainUi.IsVisible)
                _app.MainUi.Show();
        }
    }

    private async Task CaptureCoreAsync(bool forceFullMonitor)
    {
        if (_busy || _settingsOpen)
            return;

        _busy = true;
        bool mainWasVisible = _app.MainUi?.IsVisible == true;
        CursorSnapshot? cursor = null;
        var settings = _app.Settings;

        try
        {
            // Снимок курсора делаем в самом начале, до скрытия окна и задержки:
            // так его положение совпадает с моментом нажатия горячей клавиши.
            if (settings.ShowCursor)
                cursor = CursorService.Capture();

            if (mainWasVisible)
                _app.MainUi!.Hide();

            await Task.Delay(180);

            var monitors = DisplayService.GetMonitors();
            if (monitors.Count == 0)
            {
                NotifyError("Мониторы не найдены.");
                return;
            }

            if (forceFullMonitor || settings.CaptureFullMonitor)
            {
                var target = DisplayService.MonitorUnderCursor(monitors) ?? monitors[0];
                BitmapSource image;
                try
                {
                    image = CaptureService.CaptureMonitor(target);
                }
                catch (Exception ex)
                {
                    Log.Error("Не удалось захватить монитор", ex);
                    NotifyError("Не удалось захватить экран: " + ex.Message);
                    return;
                }

                if (cursor is not null)
                {
                    try
                    {
                        image = CursorService.Draw(image, cursor,
                            cursor.ScreenX - target.Bounds.Left,
                            cursor.ScreenY - target.Bounds.Top);
                        Log.Info($"Курсор добавлен на снимок монитора: {cursor.ScreenX},{cursor.ScreenY}");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Не удалось нарисовать курсор: " + ex.Message);
                    }
                }

                await ProcessImageAsync(image, target, target.Bounds, image);
                return;
            }

            var shots = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
            foreach (var monitor in monitors)
            {
                try
                {
                    shots[monitor.DeviceName] = CaptureService.CaptureMonitor(monitor);
                }
                catch (Exception ex)
                {
                    Log.Error($"Не удалось захватить {monitor.Name}", ex);
                }
            }

            if (shots.Count == 0)
            {
                NotifyError("Не удалось захватить экран.");
                return;
            }

            var selection = await SelectRegionAsync(monitors, shots, cursor);
            if (selection is null)
                return;

            var (selectedMonitor, rect) = selection.Value;
            if (!shots.TryGetValue(selectedMonitor.DeviceName, out var source))
                return;

            var screenRect = new Native.Rect
            {
                Left = selectedMonitor.Bounds.Left + rect.X,
                Top = selectedMonitor.Bounds.Top + rect.Y,
                Right = selectedMonitor.Bounds.Left + rect.X + rect.Width,
                Bottom = selectedMonitor.Bounds.Top + rect.Y + rect.Height
            };

            var cropped = CaptureService.Crop(source, rect);

            if (cursor is not null &&
                cursor.ScreenX >= screenRect.Left && cursor.ScreenX < screenRect.Right &&
                cursor.ScreenY >= screenRect.Top && cursor.ScreenY < screenRect.Bottom)
            {
                try
                {
                    cropped = CursorService.Draw(cropped, cursor,
                        cursor.ScreenX - screenRect.Left,
                        cursor.ScreenY - screenRect.Top);
                    Log.Info($"Курсор добавлен на выделенную область: {cursor.ScreenX},{cursor.ScreenY}");
                }
                catch (Exception ex)
                {
                    Log.Warn("Не удалось нарисовать курсор: " + ex.Message);
                }
            }

            await ProcessImageAsync(cropped, selectedMonitor, screenRect, source);
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка сценария захвата", ex);
            NotifyError("Не удалось сделать скриншот: " + ex.Message);
        }
        finally
        {
            _busy = false;

            if (mainWasVisible && !_app.IsExiting && _app.MainUi is not null && !_app.MainUi.IsVisible)
                _app.MainUi.Show();
        }
    }

    public void OpenSettings()
    {
        if (_settingsOpen)
        {
            if (_settingsWindow is { IsVisible: true })
                _app.ShowWindow(_settingsWindow);

            return;
        }

        _settingsOpen = true;

        try
        {
            // Настройки — дочернее окно главного. Благодаря этому в панели
            // задач остаётся один значок HDR Snip: он не исчезает и не
            // появляется заново при открытии и закрытии настроек.
            var owner = _app.MainUi;
            if (owner is not null)
                _app.ShowMainWindow();

            _settingsWindow = new SettingsWindow(_app)
            {
                ShowInTaskbar = false
            };

            if (owner is not null)
                _settingsWindow.Owner = owner;

            _settingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть настройки", ex);
            NotifyError("Не удалось открыть настройки: " + ex.Message);
        }
        finally
        {
            _settingsWindow = null;
            _settingsOpen = false;
        }
    }

    public bool TryApplySettings(AppSettings settings, out string error)
    {
        error = string.Empty;
        settings.Normalize();

        if (settings.Hotkey.IsSameAs(settings.FullMonitorHotkey))
        {
            error = "Одно и то же сочетание назначено для выделения области и для всего монитора. Выберите разные сочетания.";
            return false;
        }

        var previous = _app.Settings;
        bool hotkeyOk = _app.Hotkey?.Apply(settings.Hotkey, settings.FullMonitorHotkey) ?? true;
        if (!hotkeyOk)
        {
            _app.Hotkey?.Apply(previous.Hotkey, previous.FullMonitorHotkey);
            error = "Не удалось назначить горячую клавишу — возможно, сочетание занято другой программой. Выберите другое.";
            return false;
        }

        _app.ApplySettings(settings);
        return true;
    }

    public void OpenScreenshotsFolder()
    {
        try
        {
            OutputService.OpenFolder(_app.Settings.SaveFolder);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть папку", ex);
            NotifyError("Не удалось открыть папку: " + ex.Message);
        }
    }

    private async Task<(MonitorInfo Monitor, Int32Rect Rect)?> SelectRegionAsync(
        List<MonitorInfo> monitors,
        Dictionary<string, BitmapSource> shots,
        CursorSnapshot? cursor)
    {
        var completion = new TaskCompletionSource<(MonitorInfo Monitor, Int32Rect Rect)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var windows = new List<CaptureOverlayWindow>();

        try
        {
            foreach (var monitor in monitors)
            {
                if (!shots.TryGetValue(monitor.DeviceName, out var shot))
                    continue;

                // На стоп-кадре курсор должен быть виден сразу, ещё до выделения.
                // Рисуем его на копии кадра, а оригинал оставляем для финального кропа.
                var overlay = new CaptureOverlayWindow(monitor, WithCursor(shot, monitor, cursor));
                overlay.RegionSelected += (selectedMonitor, rect) =>
                    completion.TrySetResult((selectedMonitor, rect));
                overlay.Cancelled += () => completion.TrySetResult(null);

                windows.Add(overlay);
                overlay.ShowOverlay();
            }

            if (windows.Count == 0)
                return null;

            var cursorMonitor = DisplayService.MonitorUnderCursor(monitors);
            var active = windows.FirstOrDefault(w => ReferenceEquals(w.Monitor, cursorMonitor)) ?? windows[0];
            active.Activate();

            return await completion.Task;
        }
        finally
        {
            foreach (var window in windows)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private async Task<(MonitorInfo Monitor, Int32Rect Rect)?> SelectWindowAsync(
        List<MonitorInfo> monitors,
        Dictionary<string, BitmapSource> shots)
    {
        var completion = new TaskCompletionSource<(MonitorInfo Monitor, Int32Rect Rect)?>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var windows = new List<WindowCaptureOverlayWindow>();

        try
        {
            foreach (var monitor in monitors)
            {
                if (!shots.TryGetValue(monitor.DeviceName, out var shot))
                    continue;

                var overlay = new WindowCaptureOverlayWindow(monitor, shot);
                overlay.WindowSelected += (selectedMonitor, rect) =>
                    completion.TrySetResult((selectedMonitor, rect));
                overlay.Cancelled += () => completion.TrySetResult(null);

                windows.Add(overlay);
                overlay.ShowOverlay();
            }

            if (windows.Count == 0)
                return null;

            var cursorMonitor = DisplayService.MonitorUnderCursor(monitors);
            var active = windows.FirstOrDefault(w => ReferenceEquals(w.Monitor, cursorMonitor)) ?? windows[0];
            active.Activate();
            active.Focus();

            return await completion.Task;
        }
        finally
        {
            foreach (var window in windows)
            {
                try
                {
                    window.Close();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static BitmapSource WithCursor(BitmapSource shot, MonitorInfo monitor, CursorSnapshot? cursor)
    {
        if (cursor is null)
            return shot;

        if (cursor.ScreenX < monitor.Bounds.Left || cursor.ScreenX >= monitor.Bounds.Right ||
            cursor.ScreenY < monitor.Bounds.Top || cursor.ScreenY >= monitor.Bounds.Bottom)
        {
            return shot;
        }

        try
        {
            return CursorService.Draw(shot, cursor,
                cursor.ScreenX - monitor.Bounds.Left,
                cursor.ScreenY - monitor.Bounds.Top);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось нарисовать курсор на стоп-кадре: " + ex.Message);
            return shot;
        }
    }

    private async Task ProcessImageAsync(BitmapSource image, MonitorInfo monitor, Native.Rect screenRect,
        BitmapSource? backdrop = null)
    {
        var settings = _app.Settings;
        Log.Info($"Обработка: инструменты={settings.ShowAnnotationTools}, сохранение={settings.SaveToFile}, размер {image.PixelWidth}×{image.PixelHeight}");

        if (!settings.ShowAnnotationTools)
        {
            if (settings.SaveToFile)
                SaveImage(image, settings, copyAfterSave: true);
            else
                CopyImage(image);
            return;
        }

        var editor = new AnnotationWindow(image, backdrop, monitor, screenRect, settings);
        var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Closed += (_, _) => closed.TrySetResult(true);

        editor.Show();
        editor.Activate();
        await closed.Task;

        var result = editor.Result;
        if (result is null)
        {
            Log.Info("Редактор закрыт без сохранения.");
            return;
        }

        if (result.Action == AnnotationAction.Copy)
        {
            bool copied = TryCopyToClipboard(result.Image);
            string? savedPath = settings.SaveToFile ? TrySaveImage(result.Image, settings) : null;

            if (copied && savedPath is not null)
                Notify("HDR Snip", "Скопировано и сохранено: " + Path.GetFileName(savedPath));
            else if (copied)
                Notify("HDR Snip", "Скриншот скопирован в буфер обмена.");
            else if (savedPath is not null)
                Notify("HDR Snip", "Сохранено: " + Path.GetFileName(savedPath));
            else
                NotifyError("Не удалось скопировать или сохранить скриншот.");
        }
        else
        {
            var savedPath = TrySaveImage(result.Image, settings);
            if (savedPath is not null)
                Notify("HDR Snip", "Сохранено: " + Path.GetFileName(savedPath));
        }
    }

    private void CopyImage(BitmapSource image)
    {
        if (TryCopyToClipboard(image))
            Notify("HDR Snip", "Скриншот скопирован в буфер обмена.");
    }

    private void SaveImage(BitmapSource image, AppSettings settings, bool copyAfterSave)
    {
        var path = TrySaveImage(image, settings);
        if (path is null)
            return;

        if (copyAfterSave && TryCopyToClipboard(image))
            Notify("HDR Snip", "Скопировано и сохранено: " + Path.GetFileName(path));
        else
            Notify("HDR Snip", "Сохранено: " + Path.GetFileName(path));
    }

    private bool TryCopyToClipboard(BitmapSource image)
    {
        try
        {
            OutputService.CopyToClipboard(image);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось скопировать в буфер обмена", ex);
            NotifyError("Не удалось скопировать в буфер обмена: " + ex.Message);
            return false;
        }
    }

    private string? TrySaveImage(BitmapSource image, AppSettings settings)
    {
        try
        {
            return OutputService.SavePng(image, settings.SaveFolder);
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось сохранить скриншот", ex);
            NotifyError("Не удалось сохранить скриншот: " + ex.Message);
            return null;
        }
    }

    private void Notify(string title, string text) => _app.Tray?.Notify(title, text);

    private void NotifyError(string text) => _app.Tray?.NotifyError(text);
}
