using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Blurt;

/// <summary>Captures a key combination. Every key press is consumed so no button reacts to it.</summary>
public partial class HotkeyWindow : Window
{
    Hotkey? candidate;

    public HotkeyWindow(string target, Hotkey? current)
    {
        InitializeComponent();
        TargetName.Text = target;
        ShowKeys(current?.Parts ?? []);
        RemoveButton.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The chosen combination, or null when the key was removed.</summary>
    public Hotkey? Result { get; private set; }

    void ShowKeys(IReadOnlyList<string> keys)
    {
        Caps.ItemsSource = keys;
        Waiting.Visibility = keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            return;
        }
        if (Hotkey.IsModifier(key))
        {
            // A new combination has begun, so the one shown before can no longer be saved.
            ShowKeys(Hotkey.ModifierNames(modifiers));
            SetCandidate(null, invalid: false);
            return;
        }

        var hotkey = new Hotkey(modifiers, key);
        ShowKeys(hotkey.Parts);
        SetCandidate(hotkey.IsValid ? hotkey : null, invalid: !hotkey.IsValid);
    }

    void SetCandidate(Hotkey? hotkey, bool invalid)
    {
        candidate = hotkey;
        SaveButton.IsEnabled = candidate is not null;
        Hint.Foreground = (Brush)FindResource(invalid ? "Redline" : "Pencil");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = candidate;
        DialogResult = true;
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
