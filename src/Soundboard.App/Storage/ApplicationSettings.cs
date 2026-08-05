using Soundboard.App.Hotkeys;
using Soundboard.Audio;

namespace Soundboard.App.Storage;

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public bool SetupCompleted { get; init; }

    public bool UseDefaultMicrophone { get; init; } = true;

    public string? MicrophoneEndpointId { get; init; }

    public string? VirtualOutputEndpointId { get; init; }

    /// <summary>User slider position from zero to one, not linear gain.</summary>
    public double SoundVolume { get; init; } = 1d;

    public bool MonitoringEnabled { get; init; }

    public string? MonitorOutputEndpointId { get; init; }

    public double MonitorVolume { get; init; } = 1d;

    /// <summary>
    /// Opt-in for every installation, including upgrades: a settings file
    /// written before Voice Priority existed keeps the previous behavior.
    /// </summary>
    public bool VoicePriorityEnabled { get; init; }

    public VoiceSensitivity VoicePrioritySensitivity { get; init; } =
        VoiceSensitivity.Normal;

    public VoiceDuckingStrength VoicePriorityStrength { get; init; } =
        VoiceDuckingStrength.Balanced;

    public bool GlobalHotkeysEnabled { get; init; } = true;

    public HotkeyGesture? StopSoundHotkey { get; init; }

    /// <summary>
    /// Optional and unassigned by default so no shortcut can collide with a
    /// game or with Windows until the user chooses one.
    /// </summary>
    public HotkeyGesture? PauseResumeHotkey { get; init; }

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }

    public static ApplicationSettings Default { get; } = new();
}
