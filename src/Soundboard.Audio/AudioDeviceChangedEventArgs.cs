using NAudio.CoreAudioApi;

namespace Soundboard.Audio;

public enum AudioDeviceChangeKind
{
    Added,
    Removed,
    StateChanged,
    DefaultChanged,
    PropertyChanged
}

public sealed record AudioDeviceChangedEventArgs(
    AudioDeviceChangeKind Kind,
    string? DeviceId,
    DataFlow? DataFlow = null,
    Role? Role = null,
    DeviceState? State = null);
