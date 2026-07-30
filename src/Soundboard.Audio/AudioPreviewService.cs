using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class AudioPreviewService : IDisposable
{
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly IAudioFileDecoderFactory decoderFactory;
    private PreviewSession? currentSession;
    private bool disposed;
    private long generation;
    private float lastMaximumGainReductionDb;
    private long lastNonFiniteSampleCount;

    public AudioPreviewService(
        IAudioFileDecoderFactory? decoderFactory = null)
    {
        this.decoderFactory =
            decoderFactory ?? AudioFileDecoderFactory.Default;
    }

    public event EventHandler<string>? PreviewFailed;

    public bool IsPlaying
    {
        get
        {
            lock (syncRoot)
            {
                return currentSession is not null;
            }
        }
    }

    public float CurrentGainReductionDb
    {
        get
        {
            lock (syncRoot)
            {
                return currentSession?.Limiter.CurrentGainReductionDb ?? 0f;
            }
        }
    }

    public float MaximumGainReductionDb
    {
        get
        {
            lock (syncRoot)
            {
                return Math.Max(
                    lastMaximumGainReductionDb,
                    currentSession?.Limiter.MaximumGainReductionDb ?? 0f);
            }
        }
    }

    public long NonFiniteSampleCount
    {
        get
        {
            lock (syncRoot)
            {
                return lastNonFiniteSampleCount
                    + (currentSession?.Limiter.NonFiniteSampleCount ?? 0);
            }
        }
    }

    public Task PlayAsync(
        string filePath,
        AudioClipSettings settings,
        AudioEndpoint endpoint,
        CancellationToken cancellationToken = default)
    {
        return PlayAsync(
            filePath,
            settings,
            endpoint,
            normalizationGainDb: 0d,
            limiterEnabled: true,
            limiterCeilingDbfs: SamplePeakLimiter.DefaultCeilingDbfs,
            cancellationToken);
    }

    public async Task PlayAsync(
        string filePath,
        AudioClipSettings settings,
        AudioEndpoint endpoint,
        double normalizationGainDb,
        bool limiterEnabled,
        double limiterCeilingDbfs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(settings);
        ValidatePreviewEndpoint(endpoint);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            lock (syncRoot)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }

            Stop();
            var sessionId = Interlocked.Increment(ref generation);
            var session = await Task.Run(
                () => PreviewSession.Create(
                    sessionId,
                    filePath,
                    settings,
                    endpoint,
                    decoderFactory,
                    normalizationGainDb,
                    limiterEnabled,
                    limiterCeilingDbfs,
                    OnPlaybackStopped),
                cancellationToken);

            lock (syncRoot)
            {
                if (disposed
                    || sessionId != Volatile.Read(ref generation))
                {
                    session.Dispose();
                    throw new OperationCanceledException(
                        "The preview request was superseded.");
                }

                currentSession = session;
            }

            try
            {
                session.Start();
            }
            catch
            {
                lock (syncRoot)
                {
                    if (ReferenceEquals(currentSession, session))
                    {
                        currentSession = null;
                    }
                }

                session.Dispose();
                throw;
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref generation);
        PreviewSession? session;
        lock (syncRoot)
        {
            session = currentSession;
            currentSession = null;
        }

        if (session is not null)
        {
            CaptureLimiterDiagnostics(session);
            session.StopAndDispose();
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
        }

        Stop();
    }

    public static void ValidatePreviewEndpoint(AudioEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Direction != AudioDeviceDirection.Render
            || !endpoint.State.HasFlag(AudioEndpointState.Active))
        {
            throw new InvalidOperationException(
                "Preview requires an active render endpoint.");
        }

        if (endpoint.IsLikelyVbCable
            || AudioDeviceService.IsLikelyVbCableDeviceName(
                endpoint.FriendlyName))
        {
            throw new InvalidOperationException(
                "Preview cannot use VB-CABLE or another virtual cable. "
                + "Select a physical headphones or speakers endpoint.");
        }
    }

    private void OnPlaybackStopped(
        PreviewSession session,
        Exception? exception)
    {
        lock (syncRoot)
        {
            if (!ReferenceEquals(currentSession, session))
            {
                return;
            }

            currentSession = null;
        }

        CaptureLimiterDiagnostics(session);
        session.Dispose();
        if (exception is not null)
        {
            PreviewFailed?.Invoke(
                this,
                $"Preview stopped because audio playback failed: "
                + exception.Message);
        }
    }

    private void CaptureLimiterDiagnostics(PreviewSession session)
    {
        lock (syncRoot)
        {
            lastMaximumGainReductionDb = Math.Max(
                lastMaximumGainReductionDb,
                session.Limiter.MaximumGainReductionDb);
            lastNonFiniteSampleCount +=
                session.Limiter.NonFiniteSampleCount;
        }
    }

    private sealed class PreviewSession : IDisposable
    {
        private readonly WasapiOut output;
        private readonly MMDevice device;
        private readonly DecodedAudioSource decoded;
        private readonly Action<PreviewSession, Exception?> stopped;
        private int disposed;

        private PreviewSession(
            long id,
            WasapiOut output,
            MMDevice device,
            DecodedAudioSource decoded,
            SamplePeakLimiter limiter,
            Action<PreviewSession, Exception?> stopped)
        {
            Id = id;
            this.output = output;
            this.device = device;
            this.decoded = decoded;
            Limiter = limiter;
            this.stopped = stopped;
            output.PlaybackStopped += Output_PlaybackStopped;
        }

        public long Id { get; }

        public SamplePeakLimiter Limiter { get; }

        public static PreviewSession Create(
            long id,
            string filePath,
            AudioClipSettings settings,
            AudioEndpoint endpoint,
            IAudioFileDecoderFactory decoderFactory,
            double normalizationGainDb,
            bool limiterEnabled,
            double limiterCeilingDbfs,
            Action<PreviewSession, Exception?> stopped)
        {
            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            DecodedAudioSource? decoded = null;
            WasapiOut? output = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(endpoint.DeviceId);
                if (device.DataFlow != DataFlow.Render
                    || device.State != DeviceState.Active
                    || AudioDeviceService.IsLikelyVbCableDeviceName(
                        device.FriendlyName))
                {
                    throw new InvalidOperationException(
                        "The selected preview endpoint is unavailable or "
                        + "is not a safe physical render endpoint.");
                }

                decoded = decoderFactory.Open(filePath);
                ISampleProvider processed = new AudioClipSampleProvider(
                    decoded.SampleProvider,
                    settings);
                if (!double.IsFinite(normalizationGainDb)
                    || normalizationGainDb
                        < LoudnessNormalizationSettings.MaximumAttenuationDb
                    || normalizationGainDb
                        > LoudnessNormalizationSettings.MaximumBoostDb)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(normalizationGainDb));
                }

                var linearGain =
                    (float)Math.Pow(10d, normalizationGainDb / 20d);
                if (Math.Abs(linearGain - 1f) > 0.000001f)
                {
                    processed = new NAudio.Wave.SampleProviders
                        .VolumeSampleProvider(processed)
                    {
                        Volume = linearGain
                    };
                }

                var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                    device.AudioClient.MixFormat.SampleRate,
                    device.AudioClient.MixFormat.Channels);
                var normalized = AudioFormatNormalizer.Normalize(
                    processed,
                    targetFormat,
                    out _,
                    out _);
                var limiter = new SamplePeakLimiter(
                    normalized,
                    limiterEnabled,
                    limiterCeilingDbfs);
                output = new WasapiOut(
                    device,
                    AudioClientShareMode.Shared,
                    useEventSync: false,
                    latency: 100);
                output.Init(limiter.ToWaveProvider());
                var session = new PreviewSession(
                    id,
                    output,
                    device,
                    decoded,
                    limiter,
                    stopped);
                output = null;
                device = null;
                decoded = null;
                return session;
            }
            finally
            {
                output?.Dispose();
                decoded?.Dispose();
                device?.Dispose();
                enumerator?.Dispose();
            }
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);
            output.Play();
        }

        public void StopAndDispose()
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            try
            {
                output.Stop();
            }
            catch
            {
                // Disposal below remains authoritative.
            }

            Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            output.PlaybackStopped -= Output_PlaybackStopped;
            output.Dispose();
            decoded.Dispose();
            device.Dispose();
        }

        private void Output_PlaybackStopped(
            object? sender,
            StoppedEventArgs eventArgs)
        {
            ThreadPool.QueueUserWorkItem(
                _ => stopped(this, eventArgs.Exception));
        }
    }
}
