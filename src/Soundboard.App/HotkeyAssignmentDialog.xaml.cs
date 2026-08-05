using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soundboard.App.Hotkeys;

namespace Soundboard.App;

public partial class HotkeyAssignmentDialog : Window
{
    public HotkeyAssignmentDialog(
        string targetName,
        HotkeyGesture? currentHotkey)
    {
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);
        TargetName = targetName;
        ProposedHotkey = currentHotkey;
        CurrentHotkeyTextBlock.Text =
            currentHotkey?.DisplayText ?? "No hotkey";
        ProposedHotkeyTextBlock.Text =
            currentHotkey?.DisplayText ?? "Press a key…";
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
            SetValidation(
                "Choose a non-modifier key.",
                isProblem: true);
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
            SetValidation(
                error ?? "That combination cannot be used.",
                isProblem: true);
            SaveButton.IsEnabled = false;
            eventArgs.Handled = true;
            return;
        }

        ProposedHotkey = hotkey;
        ProposedHotkeyTextBlock.Text = hotkey!.DisplayText;
        SetValidation(
            "Available. Select Save to register it with Windows.",
            isProblem: false);
        SaveButton.IsEnabled = true;
        eventArgs.Handled = true;
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (ProposedHotkey is null)
        {
            SetValidation(
                "Capture a valid key or key combination before saving.",
                isProblem: true);
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

    private void SetValidation(string message, bool isProblem)
    {
        ValidationTextBlock.Text = message;
        ValidationTextBlock.Foreground =
            (TryFindResource(isProblem ? "ErrorBrush" : "SuccessBrush")
                as Brush)
            ?? ValidationTextBlock.Foreground;
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
