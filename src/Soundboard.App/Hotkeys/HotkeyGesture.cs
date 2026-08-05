using System.Text.Json.Serialization;

namespace Soundboard.App.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public sealed record HotkeyGesture
{
    [JsonConstructor]
    public HotkeyGesture(
        uint virtualKey,
        HotkeyModifiers modifiers,
        string displayText)
    {
        VirtualKey = virtualKey;
        Modifiers = modifiers;
        DisplayText = displayText;
    }

    public uint VirtualKey { get; init; }

    public HotkeyModifiers Modifiers { get; init; }

    public string DisplayText { get; init; }

    public static bool TryCreate(
        uint virtualKey,
        HotkeyModifiers modifiers,
        out HotkeyGesture? hotkey,
        out string? error)
    {
        var normalizedModifiers = modifiers
            & (HotkeyModifiers.Control
                | HotkeyModifiers.Alt
                | HotkeyModifiers.Shift
                | HotkeyModifiers.Windows);

        if (normalizedModifiers != modifiers)
        {
            hotkey = null;
            error = "The hotkey contains unsupported modifier flags.";
            return false;
        }

        if (!TryGetKeyName(virtualKey, out var keyName))
        {
            hotkey = null;
            error = "Choose a supported non-modifier key.";
            return false;
        }

        var displayText = Format(normalizedModifiers, keyName);
        hotkey = new HotkeyGesture(
            virtualKey,
            normalizedModifiers,
            displayText);
        error = null;
        return true;
    }

    public static bool TryNormalize(
        HotkeyGesture? serialized,
        out HotkeyGesture? hotkey,
        out string? error)
    {
        if (serialized is null)
        {
            hotkey = null;
            error = null;
            return true;
        }

        if (!TryCreate(
                serialized.VirtualKey,
                serialized.Modifiers,
                out hotkey,
                out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetKeyName(
        uint virtualKey,
        out string keyName)
    {
        if (virtualKey is >= 0x30 and <= 0x39)
        {
            keyName = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x41 and <= 0x5A)
        {
            keyName = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x60 and <= 0x69)
        {
            keyName = $"Num {virtualKey - 0x60}";
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            keyName = $"F{virtualKey - 0x6F}";
            return true;
        }

        keyName = virtualKey switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            _ => string.Empty
        };
        return keyName.Length > 0;
    }

    private static string Format(
        HotkeyModifiers modifiers,
        string keyName)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(keyName);
        return string.Join(" + ", parts);
    }
}
