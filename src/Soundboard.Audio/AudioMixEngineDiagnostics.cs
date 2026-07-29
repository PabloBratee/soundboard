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
    TimeSpan MicrophoneBufferCapacity);
