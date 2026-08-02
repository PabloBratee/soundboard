namespace Soundboard.Audio;

public sealed record AudioMixEngineDiagnostics(
    string MicrophoneFriendlyName,
    string MicrophoneEndpointId,
    string RenderFriendlyName,
    string RenderEndpointId,
    string RelatedVbCableCaptureFriendlyName,
    string RelatedVbCableCaptureEndpointId,
    AudioFormatInfo MicrophoneNativeFormat,
    AudioFormatInfo RenderMixFormat,
    AudioFormatInfo MixerTargetFormat,
    bool MicrophoneResamplingActive,
    bool MicrophoneChannelConversionActive,
    TimeSpan MicrophoneBufferCapacity)
{
    public bool MonitoringEnabled { get; init; }

    public string? MonitorFriendlyName { get; init; }

    public string? MonitorEndpointId { get; init; }

    public AudioFormatInfo? MonitorMixFormat { get; init; }

    public AudioFormatInfo? MonitorTargetFormat { get; init; }

    public bool? MonitorResamplingActive { get; init; }

    public bool? MonitorChannelConversionActive { get; init; }

    public float MonitorPeak { get; init; }

    public long ClippedSampleCount { get; init; }

    public long NonFiniteSampleCount { get; init; }

    public string MonitorInitializationStatus { get; init; } =
        "Disabled by setting";

    public string? LastMonitorWarningOrError { get; init; }

    public Guid? CurrentSoundId { get; init; }

    public long? CurrentPlaybackSessionId { get; init; }

    public int ActiveSoundCount { get; init; }
}
