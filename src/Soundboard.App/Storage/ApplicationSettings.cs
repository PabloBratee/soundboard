using Soundboard.App.Hotkeys;

namespace Soundboard.App.Storage;

public sealed record ApplicationSettings
{
    public string? MicrophoneEndpointId { get; init; }

    public string? VirtualOutputEndpointId { get; init; }

    public double MicrophoneVolume { get; init; } = 1d;

    public bool MicrophoneMuted { get; init; }

    public double SoundVolume { get; init; } = 1d;

    public bool MonitoringEnabled { get; init; }

    public string? MonitorOutputEndpointId { get; init; }

    public double MonitorVolume { get; init; } = 1d;

    public bool GlobalHotkeysEnabled { get; init; } = true;

    public HotkeyGesture? StopSoundHotkey { get; init; }

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public static ApplicationSettings Default { get; } = new();
}
