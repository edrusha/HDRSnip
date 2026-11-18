using System.Globalization;
using System.Windows.Input;

namespace HDRSnip.Models;

public sealed class HotkeyBinding
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint VkSnapshot = 0x2C;

    public HotkeyBinding(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public uint Modifiers { get; }
    public uint VirtualKey { get; }

    public bool IsPrintScreen => VirtualKey == VkSnapshot && Modifiers == 0;
    public bool IsValid => VirtualKey != 0 &&
                           (Modifiers != 0 || VirtualKey == VkSnapshot ||
                            (VirtualKey >= 0x70 && VirtualKey <= 0x87));

    public string DisplayName
    {
        get
        {
            if (IsPrintScreen)
                return "PrtScr";

            var parts = new List<string>(4);
            if ((Modifiers & ModControl) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModAlt) != 0) parts.Add("Alt");
            if ((Modifiers & ModShift) != 0) parts.Add("Shift");
            if ((Modifiers & ModWin) != 0) parts.Add("Win");

            var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
            var keyName = key switch
            {
                Key.None => "VK" + VirtualKey.ToString("X2", CultureInfo.InvariantCulture),
                Key.Snapshot => "PrtScr",
                Key.Next => "PageDown",
                Key.Prior => "PageUp",
                Key.OemPlus => "+",
                Key.OemMinus => "-",
                Key.OemComma => ",",
                Key.OemPeriod => ".",
                Key.OemQuestion => "/",
                Key.OemSemicolon => ";",
                Key.OemQuotes => "'",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemBackslash => "\\",
                Key.OemTilde => "`",
                _ => key.ToString()
            };

            parts.Add(keyName);
            return string.Join(" + ", parts);
        }
    }

    public static HotkeyBinding FromSettings(uint modifiers, uint virtualKey) => new(modifiers, virtualKey);

    public static HotkeyBinding FromKeyboard(ModifierKeys modifierKeys, Key key)
    {
        uint mods = 0;
        if ((modifierKeys & ModifierKeys.Control) != 0) mods |= ModControl;
        if ((modifierKeys & ModifierKeys.Alt) != 0) mods |= ModAlt;
        if ((modifierKeys & ModifierKeys.Shift) != 0) mods |= ModShift;
        if ((modifierKeys & ModifierKeys.Windows) != 0) mods |= ModWin;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return new HotkeyBinding(mods, vk);
    }

    public bool IsSameKey(Key key) => (uint)KeyInterop.VirtualKeyFromKey(key) == VirtualKey;

    public bool IsSameAs(HotkeyBinding other) =>
        VirtualKey == other.VirtualKey && Modifiers == other.Modifiers;

    public override string ToString() => DisplayName;
}
