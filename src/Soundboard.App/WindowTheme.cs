using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Soundboard.App;

/// <summary>
/// Asks Windows to draw the native title bar in its dark variant so the
/// standard system chrome matches the dark application surface.
/// </summary>
/// <remarks>
/// This only changes the colour of the caption Windows already draws; the
/// window keeps its real minimise, maximise, close, snap, and drag
/// behaviour. On builds that do not support the attribute the call is
/// ignored and the light caption is used.
/// </remarks>
internal static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        nint hwnd,
        int attribute,
        ref int value,
        int valueSize);

    /// <summary>
    /// Applies the dark caption once the window has a native handle.
    /// </summary>
    public static void UseDarkTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (new WindowInteropHelper(window).Handle is var handle
            && handle != nint.Zero)
        {
            Apply(handle);
            return;
        }

        window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window)
        {
            return;
        }

        window.SourceInitialized -= OnSourceInitialized;
        Apply(new WindowInteropHelper(window).Handle);
    }

    private static void Apply(nint handle)
    {
        if (handle == nint.Zero)
        {
            return;
        }

        var enabled = 1;
        try
        {
            if (DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkMode,
                    ref enabled,
                    sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    DwmwaUseImmersiveDarkModeBefore20H1,
                    ref enabled,
                    sizeof(int));
            }
        }
        catch (DllNotFoundException)
        {
            // Older or trimmed Windows installs keep the light caption.
        }
        catch (EntryPointNotFoundException)
        {
            // Same: the light caption remains, which is still usable.
        }
    }
}
