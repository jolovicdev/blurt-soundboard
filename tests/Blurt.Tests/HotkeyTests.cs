using System.Windows.Input;

namespace Blurt.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData(ModifierKeys.Control, Key.D1, true)]
    [InlineData(ModifierKeys.None, Key.F13, true)]
    [InlineData(ModifierKeys.None, Key.NumPad5, true)]
    [InlineData(ModifierKeys.None, Key.A, false)]
    [InlineData(ModifierKeys.None, Key.Space, false)]
    [InlineData(ModifierKeys.Control, Key.LeftShift, false)]
    public void PlainTypingKeysNeedAModifier(ModifierKeys modifiers, Key key, bool valid) =>
        Assert.Equal(valid, new Hotkey(modifiers, key).IsValid);

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.Back, "Ctrl+Alt+Backspace")]
    [InlineData(ModifierKeys.Shift | ModifierKeys.Control, Key.D7, "Ctrl+Shift+7")]
    [InlineData(ModifierKeys.None, Key.NumPad3, "Num3")]
    [InlineData(ModifierKeys.Alt, Key.OemMinus, "Alt+-")]
    public void ReadsLikeAKeyboardShortcut(ModifierKeys modifiers, Key key, string text) =>
        Assert.Equal(text, new Hotkey(modifiers, key).ToString());
}
