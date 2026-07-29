using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Soundboard.Audio;

public sealed class AudioMixEngine : IDisposable
{
    private static readonly TimeSpan MicrophoneBufferCapacity =
        TimeSpan.FromMilliseconds(250);

    private const int CaptureBufferMilliseconds = 50;
    private const int RenderLatencyMilliseconds = 50;
    private const int MeterNotificationsPerSecond = 10;

    private readonly object lifecycleLock = new();
    private AudioPipelineResources? resources;
    private SoundPlaybackSession? currentSound;
    private AudioMixEngineDiagnostics? diagnostics;
    private float microphoneVolume = 1f;
    private float soundVolume = 1f;
    private bool microphoneMuted;
    private bool disposed;
    private int stateValue = (int)AudioEngineState.Stopped;
    private int faultCleanupQueued;
    private long microphoneBufferOverflowCount;
    private float microphonePeak;
    private float mixedOutputPeak;

    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioEngineErrorEventArgs>? ErrorOccurred;

    public event EventHandler<AudioPeakLevelsEventArgs>? PeakLevelsChanged;

    public event EventHandler<SoundPlaybackStateChangedEventArgs>?
        SoundPlaybackStateChanged;

    public AudioEngineState State =>
        (AudioEngineState)Volatile.Read(ref stateValue);

    public AudioMixEngineDiagnostics? Diagnostics =>
        Volatile.Read(ref diagnostics);

    public long MicrophoneBufferOverflowCount =>
        Interlocked.Read(ref microphoneBufferOverflowCount);

    public bool IsSoundPlaying =>
        Volatile.Read(ref currentSound) is not null;

    public float MicrophoneVolume
    {
        get
        {
            lock (lifecycleLock)
            {
                return microphoneVolume;
            }
        }

        set
        {
            ValidateVolume(value, nameof(value));

            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                microphoneVolume = value;
                ApplyMicrophoneVolume();
            }
        }
    }

    public bool MicrophoneMuted
    {
        get
        {
            lock (lifecycleLock)
            {
                return microphoneMuted;
            }
        }

        set
        {
            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                microphoneMuted = value;
                ApplyMicrophoneVolume();
            }
        }
    }

    public float SoundVolume
    {
        get
        {
            lock (lifecycleLock)
            {
                return soundVolume;
            }
        }

        set
        {
            ValidateVolume(value, nameof(value));

            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                soundVolume = value;

                if (currentSound is not null)
                {
                    currentSound.Volume = value;
                }
            }
        }
    }

    public void Start(
        string microphoneEndpointId,
        string renderEndpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microphoneEndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderEndpointId);

        lock (lifecycleLock)
        {
            ThrowIfDisposed();

            if (State != AudioEngineState.Stopped)
            {
                throw new InvalidOperationException(
                    $"The audio engine cannot start while it is {State}.");
            }

            SetState(AudioEngineState.Starting);
            Interlocked.Exchange(ref microphoneBufferOverflowCount, 0);
            Interlocked.Exchange(ref faultCleanupQueued, 0);
            ResetPeakLevels();

            AudioPipelineResources? newResources = null;

            try
            {
                newResources = CreatePipeline(
                    microphoneEndpointId,
                    renderEndpointId);

                resources = newResources;
                diagnostics = newResources.Diagnostics;
                ApplyMicrophoneVolume();

                newResources.Output.Play();
                newResources.Capture.StartRecording();
                SetState(AudioEngineState.Running);
            }
            catch (Exception exception)
            {
                resources = null;
                diagnostics = null;
                if (newResources is not null)
                {
                    DisposePipeline(newResources);
                }
                SetState(AudioEngineState.Faulted);

                var message =
                    $"The audio engine could not start: {exception.Message}";
                ReportError(message, isRecoverable: false);
                throw new InvalidOperationException(message, exception);
            }
        }
    }

    public void Stop()
    {
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            StopCore(leaveFaulted: false);
        }
    }

    public void PlaySound(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var extension = Path.GetExtension(filePath);

        if (!extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "This milestone supports WAV and MP3 sound files only.");
        }

        lock (lifecycleLock)
        {
            ThrowIfDisposed();

            if (State != AudioEngineState.Running || resources is null)
            {
                throw new InvalidOperationException(
                    "Start the audio engine before playing a sound.");
            }

            StopSoundCore(raiseEvent: currentSound is not null);

            SoundPlaybackSession? session = null;

            try
            {
                session = new SoundPlaybackSession(
                    filePath,
                    resources.TargetFormat,
                    soundVolume);

                currentSound = session;
                resources.Mixer.AddMixerInput(session);
                RaiseSoundPlaybackStateChanged(
                    SoundPlaybackChangeReason.Started);
            }
            catch (Exception exception)
            {
                session?.Dispose();
                var message =
                    $"The sound file could not be played: {exception.Message}";
                ReportError(message, isRecoverable: true);
                throw new InvalidOperationException(message, exception);
            }
        }
    }

    public void StopSound()
    {
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            StopSoundCore(raiseEvent: currentSound is not null);
        }
    }

    public void Dispose()
    {
        lock (lifecycleLock)
        {
            if (disposed)
            {
                return;
            }

            StopCore(leaveFaulted: false);
            disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private AudioPipelineResources CreatePipeline(
        string microphoneEndpointId,
        string renderEndpointId)
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? microphoneDevice = null;
        MMDevice? renderDevice = null;
        WasapiCapture? capture = null;
        WasapiOut? output = null;

        try
        {
            microphoneDevice = enumerator.GetDevice(microphoneEndpointId);
            renderDevice = enumerator.GetDevice(renderEndpointId);

            ValidateEndpoint(
                microphoneDevice,
                DataFlow.Capture,
                "microphone");
            ValidateEndpoint(
                renderDevice,
                DataFlow.Render,
                "render");

            if (!AudioDeviceService.IsLikelyVbCableDeviceName(
                    renderDevice.FriendlyName))
            {
                throw new InvalidOperationException(
                    $"\"{renderDevice.FriendlyName}\" is a physical or "
                    + "unrecognized render endpoint. This proof of concept "
                    + "only sends audio to a likely VB-CABLE endpoint to "
                    + "prevent loud microphone feedback.");
            }

            var relatedCapture = FindRelatedVbCableCapture(enumerator)
                ?? throw new InvalidOperationException(
                    "No active VB-CABLE capture endpoint such as "
                    + "\"CABLE Output\" was detected. VB-CABLE installation "
                    + "or the required Windows restart appears incomplete.");

            var renderMixFormat = renderDevice.AudioClient.MixFormat;

            if (renderMixFormat.Channels is < 1 or > 2)
            {
                throw new NotSupportedException(
                    $"The selected output exposes {renderMixFormat.Channels} "
                    + "channels. This milestone supports mono and stereo "
                    + "output only.");
            }

            var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                renderMixFormat.SampleRate,
                renderMixFormat.Channels);

            capture = new WasapiCapture(
                microphoneDevice,
                useEventSync: true,
                CaptureBufferMilliseconds)
            {
                ShareMode = AudioClientShareMode.Shared
            };

            var microphoneNativeFormat = capture.WaveFormat;
            var bufferedCaptureFormat =
                AudioFormatNormalizer.GetBufferedCaptureFormat(
                    microphoneNativeFormat);

            var microphoneBuffer = new BufferedWaveProvider(
                bufferedCaptureFormat)
            {
                BufferDuration = MicrophoneBufferCapacity,
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            var microphoneSamples = microphoneBuffer.ToSampleProvider();
            microphoneSamples = AudioFormatNormalizer.Normalize(
                microphoneSamples,
                targetFormat,
                out var microphoneResamplingActive,
                out var microphoneChannelConversionActive);

            var microphoneVolumeProvider =
                new VolumeSampleProvider(microphoneSamples);
            var microphoneMeter = new MeteringSampleProvider(
                microphoneVolumeProvider,
                GetSamplesPerNotification(targetFormat));

            var mixer = new MixingSampleProvider(targetFormat)
            {
                ReadFully = true
            };
            mixer.AddMixerInput(microphoneMeter);
            mixer.MixerInputEnded += Mixer_MixerInputEnded;

            var outputMeter = new MeteringSampleProvider(
                mixer,
                GetSamplesPerNotification(targetFormat));

            output = new WasapiOut(
                renderDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                RenderLatencyMilliseconds);
            output.Init(outputMeter.ToWaveProvider());

            var pipelineDiagnostics = new AudioMixEngineDiagnostics(
                microphoneDevice.FriendlyName,
                microphoneDevice.ID,
                renderDevice.FriendlyName,
                renderDevice.ID,
                relatedCapture.FriendlyName,
                relatedCapture.DeviceId,
                AudioFormatInfo.FromWaveFormat(microphoneNativeFormat),
                AudioFormatInfo.FromWaveFormat(renderMixFormat),
                AudioFormatInfo.FromWaveFormat(targetFormat),
                microphoneResamplingActive,
                microphoneChannelConversionActive,
                MicrophoneBufferCapacity);

            var pipeline = new AudioPipelineResources(
                microphoneDevice,
                renderDevice,
                capture,
                output,
                microphoneBuffer,
                microphoneVolumeProvider,
                microphoneMeter,
                mixer,
                outputMeter,
                targetFormat,
                pipelineDiagnostics);

            microphoneDevice = null;
            renderDevice = null;
            capture = null;
            output = null;

            pipeline.Capture.DataAvailable += Capture_DataAvailable;
            pipeline.Capture.RecordingStopped += Capture_RecordingStopped;
            pipeline.Output.PlaybackStopped += Output_PlaybackStopped;
            microphoneMeter.StreamVolume += MicrophoneMeter_StreamVolume;
            outputMeter.StreamVolume += OutputMeter_StreamVolume;

            return pipeline;
        }
        catch
        {
            DisposeWithoutThrow(capture);
            DisposeWithoutThrow(output);
            DisposeWithoutThrow(microphoneDevice);
            DisposeWithoutThrow(renderDevice);
            throw;
        }
    }

    private static (string FriendlyName, string DeviceId)?
        FindRelatedVbCableCapture(MMDeviceEnumerator enumerator)
    {
        var devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Capture,
            DeviceState.Active);

        for (var index = 0; index < devices.Count; index++)
        {
            using var device = devices[index];

            if (AudioDeviceService.IsLikelyVbCableDeviceName(
                    device.FriendlyName))
            {
                return (device.FriendlyName, device.ID);
            }
        }

        return null;
    }

    private static void ValidateEndpoint(
        MMDevice device,
        DataFlow expectedDataFlow,
        string role)
    {
        if (device.DataFlow != expectedDataFlow)
        {
            throw new InvalidOperationException(
                $"The selected {role} endpoint has the wrong direction.");
        }

        if (!device.State.HasFlag(DeviceState.Active))
        {
            throw new InvalidOperationException(
                $"The selected {role} endpoint is not active.");
        }
    }

    private static int GetSamplesPerNotification(WaveFormat targetFormat)
    {
        return Math.Max(
            targetFormat.Channels,
            targetFormat.SampleRate
                * targetFormat.Channels
                / MeterNotificationsPerSecond);
    }

    private void Capture_DataAvailable(
        object? sender,
        WaveInEventArgs eventArgs)
    {
        try
        {
            var pipeline = resources;

            if (pipeline is null || eventArgs.BytesRecorded <= 0)
            {
                return;
            }

            var offset = 0;
            var bytesToAdd = eventArgs.BytesRecorded;
            var alignedCapacity = pipeline.MicrophoneBuffer.BufferLength
                - (pipeline.MicrophoneBuffer.BufferLength
                    % pipeline.MicrophoneBuffer.WaveFormat.BlockAlign);

            if (bytesToAdd > alignedCapacity)
            {
                offset = bytesToAdd - alignedCapacity;
                offset -= offset
                    % pipeline.MicrophoneBuffer.WaveFormat.BlockAlign;
                bytesToAdd -= offset;
            }

            var availableCapacity = pipeline.MicrophoneBuffer.BufferLength
                - pipeline.MicrophoneBuffer.BufferedBytes;

            if (bytesToAdd > availableCapacity)
            {
                pipeline.MicrophoneBuffer.ClearBuffer();
                var overflowCount = Interlocked.Increment(
                    ref microphoneBufferOverflowCount);

                if (overflowCount == 1 || overflowCount % 25 == 0)
                {
                    ReportError(
                        "Microphone buffering overflowed; stale audio was "
                        + $"cleared to keep latency bounded ({overflowCount} "
                        + "occurrence(s)).",
                        isRecoverable: true);
                }
            }

            pipeline.MicrophoneBuffer.AddSamples(
                eventArgs.Buffer,
                offset,
                bytesToAdd);
        }
        catch (Exception exception)
        {
            HandleRuntimeFault(
                $"Microphone capture failed: {exception.Message}");
        }
    }

    private void Capture_RecordingStopped(
        object? sender,
        StoppedEventArgs eventArgs)
    {
        if (State is AudioEngineState.Stopping or AudioEngineState.Stopped)
        {
            return;
        }

        var details = eventArgs.Exception?.Message
            ?? "the capture endpoint stopped unexpectedly";
        HandleRuntimeFault($"Microphone capture stopped: {details}.");
    }

    private void Output_PlaybackStopped(
        object? sender,
        StoppedEventArgs eventArgs)
    {
        if (State is AudioEngineState.Stopping or AudioEngineState.Stopped)
        {
            return;
        }

        var details = eventArgs.Exception?.Message
            ?? "the render endpoint stopped unexpectedly";
        HandleRuntimeFault($"Audio output stopped: {details}.");
    }

    private void MicrophoneMeter_StreamVolume(
        object? sender,
        StreamVolumeEventArgs eventArgs)
    {
        try
        {
            Volatile.Write(
                ref microphonePeak,
                GetPeak(eventArgs.MaxSampleValues));
            RaisePeakLevelsChanged();
        }
        catch (Exception exception)
        {
            ReportError(
                $"Microphone metering failed: {exception.Message}",
                isRecoverable: true);
        }
    }

    private void OutputMeter_StreamVolume(
        object? sender,
        StreamVolumeEventArgs eventArgs)
    {
        try
        {
            Volatile.Write(
                ref mixedOutputPeak,
                GetPeak(eventArgs.MaxSampleValues));
            RaisePeakLevelsChanged();
        }
        catch (Exception exception)
        {
            ReportError(
                $"Output metering failed: {exception.Message}",
                isRecoverable: true);
        }
    }

    private void Mixer_MixerInputEnded(
        object? sender,
        SampleProviderEventArgs eventArgs)
    {
        if (eventArgs.SampleProvider is not SoundPlaybackSession session)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ => OnSoundPlaybackCompleted(
                session,
                session.PlaybackError));
    }

    private static float GetPeak(IReadOnlyList<float> values)
    {
        var peak = 0f;

        for (var index = 0; index < values.Count; index++)
        {
            peak = Math.Max(peak, Math.Abs(values[index]));
        }

        return peak;
    }

    private void OnSoundPlaybackCompleted(
        SoundPlaybackSession session,
        Exception? exception)
    {
        lock (lifecycleLock)
        {
            if (!ReferenceEquals(currentSound, session))
            {
                session.Dispose();
                return;
            }

            resources?.Mixer.RemoveMixerInput(session);
            currentSound = null;
            session.Dispose();
            RaiseSoundPlaybackStateChanged(
                SoundPlaybackChangeReason.Completed);
        }

        if (exception is not null)
        {
            ReportError(
                $"Sound playback stopped because the file could not be "
                + $"decoded: {exception.Message}",
                isRecoverable: true);
        }
    }

    private void StopCore(bool leaveFaulted)
    {
        var currentState = State;

        if (currentState == AudioEngineState.Stopped)
        {
            return;
        }

        if (!leaveFaulted)
        {
            SetState(AudioEngineState.Stopping);
        }

        StopSoundCore(raiseEvent: currentSound is not null);

        var pipeline = resources;
        resources = null;
        diagnostics = null;
        if (pipeline is not null)
        {
            DisposePipeline(pipeline);
        }
        ResetPeakLevels();

        if (!leaveFaulted)
        {
            SetState(AudioEngineState.Stopped);
        }
    }

    private void StopSoundCore(bool raiseEvent)
    {
        var session = currentSound;
        currentSound = null;

        if (session is null)
        {
            return;
        }

        resources?.Mixer.RemoveMixerInput(session);
        session.Dispose();

        if (raiseEvent)
        {
            RaiseSoundPlaybackStateChanged(
                SoundPlaybackChangeReason.Stopped);
        }
    }

    private void ApplyMicrophoneVolume()
    {
        if (resources is not null)
        {
            resources.MicrophoneVolume.Volume =
                microphoneMuted ? 0f : microphoneVolume;
        }
    }

    private void HandleRuntimeFault(string message)
    {
        var state = State;

        if (state is AudioEngineState.Stopping
            or AudioEngineState.Stopped
            or AudioEngineState.Faulted)
        {
            return;
        }

        SetState(AudioEngineState.Faulted);
        ReportError(message, isRecoverable: false);

        if (Interlocked.Exchange(ref faultCleanupQueued, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    lock (lifecycleLock)
                    {
                        StopCore(leaveFaulted: true);
                    }
                });
        }
    }

    private void ResetPeakLevels()
    {
        Volatile.Write(ref microphonePeak, 0f);
        Volatile.Write(ref mixedOutputPeak, 0f);
        RaisePeakLevelsChanged();
    }

    private void RaisePeakLevelsChanged()
    {
        PeakLevelsChanged?.Invoke(
            this,
            new AudioPeakLevelsEventArgs(
                Volatile.Read(ref microphonePeak),
                Volatile.Read(ref mixedOutputPeak)));
    }

    private void SetState(AudioEngineState state)
    {
        var previous = (AudioEngineState)Interlocked.Exchange(
            ref stateValue,
            (int)state);

        if (previous != state)
        {
            StateChanged?.Invoke(
                this,
                new AudioEngineStateChangedEventArgs(state));
        }
    }

    private void ReportError(string message, bool isRecoverable)
    {
        ErrorOccurred?.Invoke(
            this,
            new AudioEngineErrorEventArgs(message, isRecoverable));
    }

    private void RaiseSoundPlaybackStateChanged(
        SoundPlaybackChangeReason reason)
    {
        SoundPlaybackStateChanged?.Invoke(
            this,
            new SoundPlaybackStateChangedEventArgs(reason));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (value is < 0f or > 2f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Volume must be between 0.0 and 2.0.");
        }
    }

    private void DisposePipeline(AudioPipelineResources pipeline)
    {
        pipeline.Capture.DataAvailable -= Capture_DataAvailable;
        pipeline.Capture.RecordingStopped -= Capture_RecordingStopped;
        pipeline.Output.PlaybackStopped -= Output_PlaybackStopped;
        pipeline.Mixer.MixerInputEnded -= Mixer_MixerInputEnded;
        pipeline.MicrophoneMeter.StreamVolume -=
            MicrophoneMeter_StreamVolume;
        pipeline.OutputMeter.StreamVolume -= OutputMeter_StreamVolume;
        pipeline.Dispose();
    }

    private static void DisposeWithoutThrow(IDisposable? disposable)
    {
        if (disposable is null)
        {
            return;
        }

        try
        {
            disposable.Dispose();
        }
        catch
        {
            // Cleanup remains best-effort after endpoint loss.
        }
    }

    private static void RunCleanupWithoutThrow(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch
        {
            // Continue releasing the remaining audio resources.
        }
    }

    private sealed class AudioPipelineResources : IDisposable
    {
        private bool disposed;

        public AudioPipelineResources(
            MMDevice microphoneDevice,
            MMDevice renderDevice,
            WasapiCapture capture,
            WasapiOut output,
            BufferedWaveProvider microphoneBuffer,
            VolumeSampleProvider microphoneVolume,
            MeteringSampleProvider microphoneMeter,
            MixingSampleProvider mixer,
            MeteringSampleProvider outputMeter,
            WaveFormat targetFormat,
            AudioMixEngineDiagnostics diagnostics)
        {
            MicrophoneDevice = microphoneDevice;
            RenderDevice = renderDevice;
            Capture = capture;
            Output = output;
            MicrophoneBuffer = microphoneBuffer;
            MicrophoneVolume = microphoneVolume;
            MicrophoneMeter = microphoneMeter;
            Mixer = mixer;
            OutputMeter = outputMeter;
            TargetFormat = targetFormat;
            Diagnostics = diagnostics;
        }

        public MMDevice MicrophoneDevice { get; }

        public MMDevice RenderDevice { get; }

        public WasapiCapture Capture { get; }

        public WasapiOut Output { get; }

        public BufferedWaveProvider MicrophoneBuffer { get; }

        public VolumeSampleProvider MicrophoneVolume { get; }

        public MeteringSampleProvider MicrophoneMeter { get; }

        public MixingSampleProvider Mixer { get; }

        public MeteringSampleProvider OutputMeter { get; }

        public WaveFormat TargetFormat { get; }

        public AudioMixEngineDiagnostics Diagnostics { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                Capture.StopRecording();
            }
            catch
            {
                // Device removal can make StopRecording fail. Dispose still runs.
            }

            try
            {
                Output.Stop();
            }
            catch
            {
                // Device removal can make Stop fail. Dispose still runs.
            }

            RunCleanupWithoutThrow(MicrophoneBuffer.ClearBuffer);
            RunCleanupWithoutThrow(Mixer.RemoveAllMixerInputs);
            DisposeWithoutThrow(Capture);
            DisposeWithoutThrow(Output);
            DisposeWithoutThrow(MicrophoneDevice);
            DisposeWithoutThrow(RenderDevice);
        }
    }
}
