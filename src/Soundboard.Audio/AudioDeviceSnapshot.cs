namespace Soundboard.Audio;

public sealed record AudioDeviceSnapshot(
    IReadOnlyList<AudioEndpoint> CaptureEndpoints,
    IReadOnlyList<AudioEndpoint> RenderEndpoints,
    IReadOnlyList<string> Warnings);
