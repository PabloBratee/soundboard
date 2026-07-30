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
        float virtualVolume)
    {
        SoundId = soundId;
        SessionId = sessionId;
        VirtualBranch = new SoundPlaybackBranch(
            SoundPlaybackBranchRole.VirtualOutput,
            filePath,
            clipSettings,
            decoderFactory,
            virtualTargetFormat,
            virtualVolume);
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

    public void SetVirtualVolume(float volume)
    {
        VirtualBranch.Volume = volume;
    }

    public void SetMonitorVolume(float volume)
    {
        MonitorBranch?.SetVolume(volume);
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
    private readonly VolumeSampleProvider volumeProvider;
    private Exception? playbackError;
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

            var clipped = new AudioClipSampleProvider(
                decodedSource.SampleProvider,
                clipSettings);
            var normalized = AudioFormatNormalizer.Normalize(
                clipped,
                targetFormat,
                out var resamplingActive,
                out var channelConversionActive);

            ResamplingActive = resamplingActive;
            ChannelConversionActive = channelConversionActive;
            volumeProvider = new VolumeSampleProvider(normalized)
            {
                Volume = volume
            };
        }
        catch
        {
            decodedSource.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => volumeProvider.WaveFormat;

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
                return volumeProvider.Volume;
            }
        }

        set
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                volumeProvider.Volume = value;
            }
        }
    }

    public void SetVolume(float volume)
    {
        lock (syncRoot)
        {
            if (!disposed)
            {
                volumeProvider.Volume = volume;
            }
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

            try
            {
                return volumeProvider.Read(buffer, offset, count);
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
