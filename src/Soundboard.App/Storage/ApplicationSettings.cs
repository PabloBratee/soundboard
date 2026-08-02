using Soundboard.App.Hotkeys;
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

    public bool GlobalHotkeysEnabled { get; init; } = true;

    public HotkeyGesture? StopSoundHotkey { get; init; }

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public bool WindowMaximized { get; init; }

    public static ApplicationSettings Default { get; } = new();
}
