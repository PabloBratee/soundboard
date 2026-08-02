namespace Soundboard.Audio;

public sealed record AudioEndpoint(
    string DeviceId,
    string FriendlyName,
    AudioDeviceDirection Direction,
    AudioEndpointState State,
    bool IsDefault,
    bool IsLikelyVbCable,
    string? InterfaceFriendlyName = null,
    string? EndpointDescription = null,
    string? ControllerDeviceId = null,
    string? InterfacePath = null);
