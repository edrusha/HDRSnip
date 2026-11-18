using System.Runtime.InteropServices;
using System.Windows.Threading;
using HDRSnip.Interop;
using HDRSnip.Models;

namespace HDRSnip.Services;

public sealed class HotkeyService : IDisposable
{
    public const int RegionHotkeyId = 0x4853;
    public const int FullMonitorHotkeyId = 0x4854;

    private readonly IntPtr _hwnd;
    private readonly Dispatcher _dispatcher;
    private readonly List<int> _registeredIds = new();
    private Native.LowLevelKeyboardProc? _hookProc;
    private IntPtr _hook;
    private HotkeyBinding _regionBinding = new(0, HotkeyBinding.VkSnapshot);
    private HotkeyBinding _fullMonitorBinding = new(HotkeyBinding.ModControl, HotkeyBinding.VkSnapshot);
    private long _lastRegionTrigger;
    private long _lastFullMonitorTrigger;

    public HotkeyService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public event Action? Pressed;
    public event Action? FullMonitorPressed;

    public string? LastError { get; private set; }

    public bool Apply(HotkeyBinding regionBinding, HotkeyBinding fullMonitorBinding)
    {
        UnregisterAll();
        _regionBinding = regionBinding;
        _fullMonitorBinding = fullMonitorBinding;
        LastError = null;

        bool regionViaHook = RegisterOrDefer(RegionHotkeyId, regionBinding);
        bool fullViaHook = RegisterOrDefer(FullMonitorHotkeyId, fullMonitorBinding);

        if (!regionViaHook && !fullViaHook)
            return true;

        if (InstallHook())
        {
            Log.Info($"Горячие клавиши готовы: {regionBinding.DisplayName} — область, " +
                     $"{fullMonitorBinding.DisplayName} — весь монитор.");
            return true;
        }

        LastError ??= "Не удалось установить клавиатурный хук.";
        Log.Error("Не удалось назначить горячие клавиши: " + LastError);
        UnregisterAll();
        return false;
    }

    private bool RegisterOrDefer(int id, HotkeyBinding binding)
    {
        if (binding.Modifiers != 0 &&
            Native.RegisterHotKey(_hwnd, id, binding.Modifiers | Native.ModNoRepeat, binding.VirtualKey))
        {
            _registeredIds.Add(id);
            Log.Info("Горячая клавиша зарегистрирована: " + binding.DisplayName);
            return false;
        }

        if (binding.Modifiers != 0)
        {
            LastError = "RegisterHotKey: " + Marshal.GetLastWin32Error();
            Log.Warn("RegisterHotKey не сработал для " + binding.DisplayName + ", ставлю клавиатурный хук.");
        }

        return true;
    }

    public void Raise()
    {
        RaiseEvent(Pressed, "горячей клавиши выделения");
    }

    public void RaiseFullMonitor()
    {
        RaiseEvent(FullMonitorPressed, "горячей клавиши всего монитора");
    }

    private void RaiseEvent(Action? handler, string description)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка обработчика " + description, ex);
        }
    }

    private bool InstallHook()
    {
        try
        {
            _hookProc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WhKeyboardLl, _hookProc, Native.GetModuleHandle(null), 0);
            if (_hook != IntPtr.Zero)
                return true;

            LastError = "SetWindowsHookEx: " + Marshal.GetLastWin32Error();
            _hookProc = null;
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _hookProc = null;
            return false;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = wParam.ToInt32();
            if (message is Native.WmKeyDown or Native.WmSysKeyDown)
            {
                var data = Marshal.PtrToStructure<Native.KeyboardHookData>(lParam);
                if (Matches(_regionBinding, data.VirtualKey))
                {
                    if (ShouldTrigger(ref _lastRegionTrigger))
                        _dispatcher.BeginInvoke(new Action(Raise));

                    return new IntPtr(1);
                }

                if (Matches(_fullMonitorBinding, data.VirtualKey))
                {
                    if (ShouldTrigger(ref _lastFullMonitorTrigger))
                        _dispatcher.BeginInvoke(new Action(RaiseFullMonitor));

                    return new IntPtr(1);
                }
            }
        }

        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool ShouldTrigger(ref long lastTrigger)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref lastTrigger);
        if (now - previous <= 300)
            return false;

        Interlocked.Exchange(ref lastTrigger, now);
        return true;
    }

    private bool Matches(HotkeyBinding binding, uint virtualKey)
    {
        if (virtualKey != binding.VirtualKey)
            return false;

        uint current = 0;
        if (IsDown(Native.VkControl)) current |= HotkeyBinding.ModControl;
        if (IsDown(Native.VkMenu)) current |= HotkeyBinding.ModAlt;
        if (IsDown(Native.VkShift)) current |= HotkeyBinding.ModShift;
        if (IsDown(Native.VkLWin) || IsDown(Native.VkRWin)) current |= HotkeyBinding.ModWin;
        return current == binding.Modifiers;
    }

    private static bool IsDown(int virtualKey) =>
        (Native.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void UnregisterAll()
    {
        foreach (int id in _registeredIds)
        {
            try
            {
                Native.UnregisterHotKey(_hwnd, id);
            }
            catch
            {
                // ignore
            }
        }

        _registeredIds.Clear();

        if (_hook != IntPtr.Zero)
        {
            try
            {
                Native.UnhookWindowsHookEx(_hook);
            }
            catch
            {
                // ignore
            }

            _hook = IntPtr.Zero;
        }

        _hookProc = null;
    }

    public void Dispose()
    {
        UnregisterAll();
    }
}
