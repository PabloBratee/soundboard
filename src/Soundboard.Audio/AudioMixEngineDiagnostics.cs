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

    public bool SafetyLimiterEnabled { get; init; } = true;

    public double SafetyLimiterCeilingDbfs { get; init; } =
        SamplePeakLimiter.DefaultCeilingDbfs;

    public TimeSpan SafetyLimiterLookahead { get; init; } =
        SamplePeakLimiter.DefaultLookahead;

    public float VirtualLimiterCurrentGainReductionDb { get; init; }

    public float VirtualLimiterMaximumGainReductionDb { get; init; }

    public float MonitorLimiterCurrentGainReductionDb { get; init; }

    public float MonitorLimiterMaximumGainReductionDb { get; init; }

    public long LimiterNonFiniteSampleCount { get; init; }

    public string MonitorInitializationStatus { get; init; } =
        "Disabled by setting";

    public string? LastMonitorWarningOrError { get; init; }

    public Guid? CurrentSoundId { get; init; }

    public long? CurrentPlaybackSessionId { get; init; }
}
