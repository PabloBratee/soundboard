namespace Soundboard.Audio;

public sealed record AudioMonitorConfiguration(
    bool Enabled,
    string? EndpointId);
