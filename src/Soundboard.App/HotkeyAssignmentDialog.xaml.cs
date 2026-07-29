using System.Windows;
using System.Windows.Input;
using Soundboard.App.Hotkeys;

namespace Soundboard.App;

public partial class HotkeyAssignmentDialog : Window
{
    public HotkeyAssignmentDialog(
        string targetName,
        HotkeyGesture? currentHotkey)
    {
        InitializeComponent();
        TargetName = targetName;
        ProposedHotkey = currentHotkey;
        ProposedHotkeyTextBlock.Text =
            currentHotkey?.DisplayText ?? "Press a combination…";
        SaveButton.IsEnabled = currentHotkey is not null;
        Loaded += (_, _) => CaptureArea.Focus();
    }

    public string TargetName { get; }

    public HotkeyGesture? ProposedHotkey { get; private set; }

    public bool ClearRequested { get; private set; }

    private void CaptureArea_PreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape
            && Keyboard.Modifiers == ModifierKeys.None)
        {
            DialogResult = false;
            eventArgs.Handled = true;
            return;
        }

        var key = eventArgs.Key == Key.System
            ? eventArgs.SystemKey
            : eventArgs.Key;
        if (key is Key.LeftCtrl
            or Key.RightCtrl
            or Key.LeftAlt
            or Key.RightAlt
            or Key.LeftShift
            or Key.RightShift
            or Key.LWin
            or Key.RWin
            or Key.None)
        {
            ValidationTextBlock.Text =
                "Press a non-modifier key while holding any modifiers.";
            eventArgs.Handled = true;
            return;
        }

        var modifiers = ConvertModifiers(Keyboard.Modifiers);
        var virtualKey = unchecked((uint)KeyInterop.VirtualKeyFromKey(key));
        if (!HotkeyGesture.TryCreate(
                virtualKey,
                modifiers,
                out var hotkey,
                out var error))
        {
            ProposedHotkey = null;
            ProposedHotkeyTextBlock.Text = "Invalid combination";
            ValidationTextBlock.Text = error;
            SaveButton.IsEnabled = false;
            eventArgs.Handled = true;
            return;
        }

        ProposedHotkey = hotkey;
        ProposedHotkeyTextBlock.Text = hotkey!.DisplayText;
        ValidationTextBlock.Text =
            "Proposed combination captured. Select Save to verify it "
            + "with Windows.";
        SaveButton.IsEnabled = true;
        eventArgs.Handled = true;
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ProposedHotkey is null)
        {
            ValidationTextBlock.Text =
                "Capture a valid combination before saving.";
            CaptureArea.Focus();
            return;
        }

        DialogResult = true;
    }

    private void ClearButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ProposedHotkey = null;
        ClearRequested = true;
        DialogResult = true;
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        DialogResult = false;
    }

    private static HotkeyModifiers ConvertModifiers(
        ModifierKeys modifiers)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }
}
