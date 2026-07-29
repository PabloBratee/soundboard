namespace Soundboard.App.Storage;

public sealed record ApplicationSettings(
    string? MicrophoneEndpointId,
    string? VirtualOutputEndpointId,
    double MicrophoneVolume,
    bool MicrophoneMuted,
    double SoundVolume,
    double? WindowLeft,
    double? WindowTop,
    double? WindowWidth,
    double? WindowHeight)
{
    public static ApplicationSettings Default { get; } = new(
        MicrophoneEndpointId: null,
        VirtualOutputEndpointId: null,
        MicrophoneVolume: 1d,
        MicrophoneMuted: false,
        SoundVolume: 1d,
        WindowLeft: null,
        WindowTop: null,
        WindowWidth: null,
        WindowHeight: null);
}
