namespace Soundboard.Audio;

public sealed class AudioEngineStateChangedEventArgs(
    AudioEngineState state,
    long sessionId) : EventArgs
{
    public AudioEngineState State { get; } = state;

    public long SessionId { get; } = sessionId;
}

public sealed class AudioEngineErrorEventArgs(
    string message,
    bool isRecoverable,
    long sessionId) : EventArgs
{
    public string Message { get; } = message;

    public bool IsRecoverable { get; } = isRecoverable;

    public long SessionId { get; } = sessionId;
}

public sealed class AudioPeakLevelsEventArgs(
    float microphonePeak,
    float mixedOutputPeak,
    float monitorOutputPeak,
    bool voiceDuckingActive = false) : EventArgs
{
    public float MicrophonePeak { get; } = microphonePeak;

    public float MixedOutputPeak { get; } = mixedOutputPeak;

    public float MonitorOutputPeak { get; } = monitorOutputPeak;

    /// <summary>
    /// True while Voice Priority is lowering sounds. Carried with the
    /// existing meter notifications so the status needs no extra timer.
    /// </summary>
    public bool VoiceDuckingActive { get; } = voiceDuckingActive;
}

public enum SoundPlaybackChangeReason
{
    Started,
    Completed,
    Stopped
}

public sealed class SoundPlaybackStateChangedEventArgs(
    SoundPlaybackChangeReason reason,
    Guid soundId,
    long sessionId) : EventArgs
{
    public SoundPlaybackChangeReason Reason { get; } = reason;

    public Guid SoundId { get; } = soundId;

    public long SessionId { get; } = sessionId;

    public bool IsPlaying => Reason == SoundPlaybackChangeReason.Started;
}
