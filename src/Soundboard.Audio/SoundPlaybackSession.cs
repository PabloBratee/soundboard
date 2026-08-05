using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

internal enum SoundPlaybackBranchRole
{
    VirtualOutput,
    MonitorOutput
}

internal sealed class SoundPlaybackSession : IDisposable
{
    private readonly object syncRoot = new();
    private SoundPlaybackBranch? monitorBranch;
    private readonly float perSoundGain;
    private float masterGain;
    private float monitorGain;
    private bool paused;
    private bool disposed;
    private int authoritativeCompletionQueued;
    private int monitorCompletionQueued;

    public SoundPlaybackSession(
        Guid soundId,
        long sessionId,
        string filePath,
        AudioClipSettings clipSettings,
        IAudioFileDecoderFactory decoderFactory,
        WaveFormat virtualTargetFormat,
        double volumePercent,
        float masterGain,
        float monitorGain)
    {
        SoundId = soundId;
        SessionId = sessionId;
        perSoundGain = AudioGain.FromPercent(volumePercent);
        this.masterGain = masterGain;
        this.monitorGain = monitorGain;
        VirtualBranch = new SoundPlaybackBranch(
            SoundPlaybackBranchRole.VirtualOutput,
            filePath,
            clipSettings,
            decoderFactory,
            virtualTargetFormat,
            perSoundGain * masterGain);
    }

    public Guid SoundId { get; }

    public long SessionId { get; }

    public SoundPlaybackBranch VirtualBranch { get; }

    public SoundPlaybackBranch? MonitorBranch
    {
        get
        {
            lock (syncRoot)
            {
                return monitorBranch;
            }
        }
    }

    /// <summary>
    /// True while this session is held at its exact current sample position
    /// by the global paused state.
    /// </summary>
    public bool IsPaused
    {
        get
        {
            lock (syncRoot)
            {
                return paused;
            }
        }
    }

    public void AttachMonitorBranch(SoundPlaybackBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);

        if (branch.Role != SoundPlaybackBranchRole.MonitorOutput)
        {
            throw new ArgumentException(
                "The branch must target the monitor output.",
                nameof(branch));
        }

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (monitorBranch is not null)
            {
                throw new InvalidOperationException(
                    "The playback session already has a monitor branch.");
            }

            monitorBranch = branch;
            branch.SetPaused(paused);
        }
    }

    /// <summary>
    /// Freezes or continues both branches. A paused branch stops advancing
    /// through its source data instead of playing silently, so resuming
    /// continues from the exact previous sample position.
    /// </summary>
    public void SetPaused(bool value)
    {
        lock (syncRoot)
        {
            paused = value;
            VirtualBranch.SetPaused(value);
            monitorBranch?.SetPaused(value);
        }
    }

    public SoundPlaybackBranch? DetachMonitorBranch()
    {
        lock (syncRoot)
        {
            var branch = monitorBranch;
            monitorBranch = null;
            return branch;
        }
    }

    public bool TryQueueAuthoritativeCompletion()
    {
        return Interlocked.Exchange(
            ref authoritativeCompletionQueued,
            1) == 0;
    }

    public bool TryQueueMonitorCompletion()
    {
        return Interlocked.Exchange(ref monitorCompletionQueued, 1) == 0;
    }

    public float MonitorBranchGain => perSoundGain * masterGain * monitorGain;

    public void SetMasterVolume(float volume)
    {
        masterGain = volume;
        VirtualBranch.Volume = perSoundGain * masterGain;
        MonitorBranch?.SetVolume(MonitorBranchGain);
    }

    public void SetMonitorVolume(float volume)
    {
        monitorGain = volume;
        MonitorBranch?.SetVolume(MonitorBranchGain);
    }

    public void Dispose()
    {
        SoundPlaybackBranch? branch;

        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            branch = monitorBranch;
            monitorBranch = null;
        }

        VirtualBranch.Dispose();
        branch?.Dispose();
    }
}

internal sealed class SoundPlaybackBranch : ISampleProvider, IDisposable
{
    private readonly object syncRoot = new();
    private readonly DecodedAudioSource decodedSource;
    private readonly SmoothGainSampleProvider gainProvider;
    private Exception? playbackError;
    private bool paused;
    private bool disposed;

    public SoundPlaybackBranch(
        SoundPlaybackBranchRole role,
        string filePath,
        AudioClipSettings clipSettings,
        IAudioFileDecoderFactory decoderFactory,
        WaveFormat targetFormat,
        float volume)
    {
        ArgumentNullException.ThrowIfNull(clipSettings);
        Role = role;
        decodedSource = decoderFactory.Open(filePath);

        try
        {
            if (decodedSource.Duration != clipSettings.SourceDuration)
            {
                throw new InvalidDataException(
                    "The decoded source duration no longer matches the "
                    + "library clip metadata.");
            }

            ISampleProvider processed = new AudioClipSampleProvider(
                decodedSource.SampleProvider,
                clipSettings);
            var normalized = AudioFormatNormalizer.Normalize(
                processed,
                targetFormat,
                out var resamplingActive,
                out var channelConversionActive);

            ResamplingActive = resamplingActive;
            ChannelConversionActive = channelConversionActive;
            gainProvider = new SmoothGainSampleProvider(normalized, volume);
        }
        catch
        {
            decodedSource.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => gainProvider.WaveFormat;

    public SoundPlaybackBranchRole Role { get; }

    public bool ResamplingActive { get; }

    public bool ChannelConversionActive { get; }

    public Exception? PlaybackError
    {
        get
        {
            lock (syncRoot)
            {
                return playbackError;
            }
        }
    }

    public float Volume
    {
        get
        {
            lock (syncRoot)
            {
                return gainProvider.Gain;
            }
        }

        set
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                gainProvider.SetGain(value);
            }
        }
    }

    public void SetVolume(float volume)
    {
        lock (syncRoot)
        {
            if (!disposed)
            {
                gainProvider.SetGain(volume);
            }
        }
    }

    public void SetPaused(bool value)
    {
        lock (syncRoot)
        {
            paused = value;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return 0;
            }

            // A paused branch keeps its place in the mix and its exact source
            // position: nothing is decoded, so no audio is consumed and
            // silently discarded.
            if (paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            try
            {
                return gainProvider.Read(buffer, offset, count);
            }
            catch (Exception exception)
            {
                playbackError = exception;
                return 0;
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            decodedSource.Dispose();
        }
    }
}

internal sealed class SmoothGainSampleProvider : ISampleProvider
{
    private const int RampMilliseconds = 5;

    private readonly ISampleProvider source;
    private readonly int rampFrameCount;
    private float currentGain;
    private float targetGain;
    private int remainingRampFrames;

    public SmoothGainSampleProvider(ISampleProvider source, float initialGain)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        ValidateGain(initialGain);
        currentGain = initialGain;
        targetGain = initialGain;
        rampFrameCount = Math.Max(
            1,
            source.WaveFormat.SampleRate * RampMilliseconds / 1000);
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public float Gain => targetGain;

    public void SetGain(float gain)
    {
        ValidateGain(gain);
        if (gain == targetGain)
        {
            return;
        }

        targetGain = gain;
        remainingRampFrames = rampFrameCount;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        var completeFrameSamples = read - (read % channels);

        for (var sampleOffset = 0;
             sampleOffset < completeFrameSamples;
             sampleOffset += channels)
        {
            AdvanceRamp();
            for (var channel = 0; channel < channels; channel++)
            {
                buffer[offset + sampleOffset + channel] *= currentGain;
            }
        }

        for (var sampleOffset = completeFrameSamples;
             sampleOffset < read;
             sampleOffset++)
        {
            buffer[offset + sampleOffset] *= currentGain;
        }

        return read;
    }

    private void AdvanceRamp()
    {
        if (remainingRampFrames <= 0)
        {
            currentGain = targetGain;
            return;
        }

        currentGain +=
            (targetGain - currentGain) / remainingRampFrames;
        remainingRampFrames--;
    }

    private static void ValidateGain(float gain)
    {
        if (!float.IsFinite(gain) || gain is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gain),
                "Gain must be finite and between silence and unity.");
        }
    }
}
