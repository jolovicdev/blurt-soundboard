using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Blurt;

public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    /// <summary>
    /// A combination needs a modifier, except function keys and the numeric keypad, which most
    /// games and chat boxes leave alone.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        !IsModifier(Key)
        && (Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24 || Key is >= Key.NumPad0 and <= Key.Divide);

    public static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    /// <summary>The keys to press, in the usual order: "Ctrl", "Alt", "1".</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Parts => [.. ModifierNames(Modifiers), KeyName(Key)];

    public override string ToString() => string.Join('+', Parts);

    public static List<string> ModifierNames(ModifierKeys modifiers)
    {
        var names = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) names.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) names.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) names.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) names.Add("Win");
        return names;
    }

    static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"Num{key - Key.NumPad0}",
        Key.Multiply => "Num*",
        Key.Add => "Num+",
        Key.Subtract => "Num-",
        Key.Divide => "Num/",
        Key.Decimal => "Num.",
        Key.Back => "Backspace",
        Key.Return => "Enter",
        Key.Prior => "PageUp",
        Key.Next => "PageDown",
        Key.Capital => "CapsLock",
        Key.Oem1 => ";",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemMinus => "-",
        Key.OemPeriod => ".",
        Key.Oem2 => "/",
        Key.Oem3 => "`",
        Key.Oem4 => "[",
        Key.Oem5 => "\\",
        Key.Oem6 => "]",
        Key.Oem7 => "'",
        _ => key.ToString(),
    };
}

/// <summary>
/// System-wide hotkeys, which fire while a game or the voice chat app has focus.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    const int WmHotkey = 0x0312;
    const uint ModNoRepeat = 0x4000;

    readonly HwndSource source;
    readonly Dictionary<int, Action> actions = [];
    int nextId;

    public GlobalHotkeys(Window window)
    {
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())
            ?? throw new InvalidOperationException("The window has no handle.");
        source.AddHook(Hook);
    }

    /// <summary>Returns false when another application already owns the combination.</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        int id = ++nextId;
        // ModifierKeys uses the same bit values as the MOD_ALT, MOD_CONTROL, MOD_SHIFT and MOD_WIN flags.
        uint modifiers = (uint)hotkey.Modifiers | ModNoRepeat;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (!RegisterHotKey(source.Handle, id, modifiers, virtualKey)) return false;
        actions[id] = action;
        return true;
    }

    /// <remarks>Ids restart from 1, since RegisterHotKey only accepts application ids up to 0xBFFF.</remarks>
    public void Clear()
    {
        foreach (var id in actions.Keys) UnregisterHotKey(source.Handle, id);
        actions.Clear();
        nextId = 0;
    }

    public void Dispose()
    {
        Clear();
        source.RemoveHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && actions.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
