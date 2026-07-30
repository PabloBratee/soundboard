using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Soundboard.App;

/// <summary>
/// Hosts the advanced audio, monitoring, hotkey, loudness, and diagnostic
/// controls that used to crowd the main soundboard.
/// </summary>
/// <remarks>
/// The window is created once for the lifetime of the application and is
/// hidden rather than destroyed, so every control instance and its event
/// wiring survives across open and close. Showing or hiding this window
/// therefore never touches the audio engine, device selection, or hotkey
/// registrations.
/// </remarks>
public partial class SettingsWindow : Window
{
    private bool allowClose;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        Closing += SettingsWindow_Closing;
    }

    /// <summary>
    /// Permits the window to close for real during application shutdown.
    /// </summary>
    public void AllowRealClose()
    {
        allowClose = true;
    }

    /// <summary>
    /// Shows the window, or brings an already-open window to the front.
    /// </summary>
    public void ShowOrActivate(Window owner)
    {
        if (Owner is null && !ReferenceEquals(owner, this))
        {
            Owner = owner;
        }

        if (IsVisible)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            _ = Activate();
            return;
        }

        Show();
        _ = Activate();
    }

    private void CloseSettingsButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        Hide();
    }

    private void SettingsWindow_PreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            Hide();
        }
    }

    private void SettingsWindow_Closing(
        object? sender,
        CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }
}
