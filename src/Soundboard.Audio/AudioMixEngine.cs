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
    private readonly IAudioFileDecoderFactory decoderFactory;
    private AudioPipelineResources? resources;
    private readonly Dictionary<Guid, SoundPlaybackSession> activeSounds = [];
    private Guid? mostRecentSoundId;
    private AudioMixEngineDiagnostics? diagnostics;
    private float soundVolume = 1f;
    private float monitorVolume = 1f;
    private bool disposed;
    private int stateValue = (int)AudioEngineState.Stopped;
    private int faultCleanupQueued;
    private int monitorFaultCleanupQueued;
    private long soundSessionGeneration;
    private long microphoneBufferOverflowCount;
    private float microphonePeak;
    private float mixedOutputPeak;
    private float monitorOutputPeak;

    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<AudioEngineErrorEventArgs>? ErrorOccurred;

    public event EventHandler<AudioPeakLevelsEventArgs>? PeakLevelsChanged;

    public event EventHandler<SoundPlaybackStateChangedEventArgs>?
        SoundPlaybackStateChanged;

    public AudioMixEngine(
        IAudioFileDecoderFactory? decoderFactory = null)
    {
        this.decoderFactory =
            decoderFactory ?? AudioFileDecoderFactory.Default;
    }

    public AudioEngineState State =>
        (AudioEngineState)Volatile.Read(ref stateValue);

    public AudioMixEngineDiagnostics? Diagnostics =>
        Volatile.Read(ref diagnostics);

    public long MicrophoneBufferOverflowCount =>
        Interlocked.Read(ref microphoneBufferOverflowCount);

    public bool IsSoundPlaying
    {
        get
        {
            lock (lifecycleLock)
            {
                return activeSounds.Count > 0;
            }
        }
    }

    public Guid? CurrentSoundId
    {
        get
        {
            lock (lifecycleLock)
            {
                return mostRecentSoundId;
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

                foreach (var session in activeSounds.Values)
                {
                    session.SetMasterVolume(value);
                }
            }
        }
    }

    public float MonitorVolume
    {
        get
        {
            lock (lifecycleLock)
            {
                return monitorVolume;
            }
        }

        set
        {
            ValidateVolume(value, nameof(value));

            lock (lifecycleLock)
            {
                ThrowIfDisposed();
                monitorVolume = value;
                foreach (var session in activeSounds.Values)
                {
                    session.SetMonitorVolume(value);
                }
            }
        }
    }

    public void Start(
        string microphoneEndpointId,
        string renderEndpointId,
        AudioMonitorConfiguration monitorConfiguration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microphoneEndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderEndpointId);
        ArgumentNullException.ThrowIfNull(monitorConfiguration);

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
            Interlocked.Exchange(ref monitorFaultCleanupQueued, 0);
            ResetPeakLevels();

            AudioPipelineResources? newResources = null;

            try
            {
                newResources = CreatePipeline(
                    microphoneEndpointId,
                    renderEndpointId,
                    monitorConfiguration);

                resources = newResources;
                diagnostics = newResources.Diagnostics;
                newResources.Output.Play();
                if (newResources.Monitor is not null)
                {
                    try
                    {
                        newResources.Monitor.Output.Play();
                    }
                    catch (Exception exception)
                    {
                        DisableMonitorPipelineCore(
                            newResources,
                            "Monitoring could not start and was disabled for "
                            + $"this engine session: {exception.Message}",
                            reportError: false);
                    }
                }

                newResources.Capture.StartRecording();
                SetState(AudioEngineState.Running);

                if (diagnostics?.LastMonitorWarningOrError is { } warning)
                {
                    ReportError(warning, isRecoverable: true);
                }
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

    public void PlaySound(
        Guid soundId,
        string filePath,
        AudioClipSettings clipSettings,
        double volumePercent = 100d)
    {
        if (soundId == Guid.Empty)
        {
            throw new ArgumentException(
                "A stable sound ID is required.",
                nameof(soundId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(clipSettings);
        if (!double.IsFinite(volumePercent)
            || volumePercent is < 0d or > 100d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(volumePercent),
                "Sound volume must be between 0 and 100 percent.");
        }

        lock (lifecycleLock)
        {
            ThrowIfDisposed();

            if (State != AudioEngineState.Running || resources is null)
            {
                throw new InvalidOperationException(
                    "Start the audio engine before playing a sound.");
            }

            StopSoundCore(soundId, raiseEvent: true);

            SoundPlaybackSession? session = null;

            try
            {
                var sessionId = Interlocked.Increment(
                    ref soundSessionGeneration);
                session = new SoundPlaybackSession(
                    soundId,
                    sessionId,
                    filePath,
                    clipSettings,
                    decoderFactory,
                    resources.TargetFormat,
                    volumePercent,
                    soundVolume,
                    monitorVolume);

                activeSounds.Add(soundId, session);
                mostRecentSoundId = soundId;
                resources.Mixer.AddMixerInput(session.VirtualBranch);

                if (resources.Monitor is not null)
                {
                    try
                    {
                        var monitorBranch = new SoundPlaybackBranch(
                            SoundPlaybackBranchRole.MonitorOutput,
                            filePath,
                            clipSettings,
                            decoderFactory,
                            resources.Monitor.TargetFormat,
                            session.MonitorBranchGain);
                        session.AttachMonitorBranch(monitorBranch);
                        resources.Monitor.Mixer.AddMixerInput(monitorBranch);
                    }
                    catch (Exception exception)
                    {
                        DisableMonitorPipelineCore(
                            resources,
                            "Sound monitoring failed while preparing the "
                            + "sound and was disabled for this engine session. "
                            + "The sound will still be sent to the virtual "
                            + $"microphone: {exception.Message}");
                    }
                }

                UpdatePlaybackDiagnostics(session);
                RaiseSoundPlaybackStateChanged(
                    SoundPlaybackChangeReason.Started,
                    session);
            }
            catch (Exception exception)
            {
                if (session is not null)
                {
                    activeSounds.Remove(soundId);
                    if (mostRecentSoundId == soundId)
                    {
                        mostRecentSoundId = activeSounds.Count == 0
                            ? null
                            : activeSounds.Values
                                .MaxBy(item => item.SessionId)?.SoundId;
                    }

                    resources.Mixer.RemoveMixerInput(
                        session.VirtualBranch);
                    var monitorBranch = session.DetachMonitorBranch();
                    if (monitorBranch is not null)
                    {
                        resources.Monitor?.Mixer.RemoveMixerInput(
                            monitorBranch);
                        monitorBranch.Dispose();
                    }

                    session.Dispose();
                    ClearPlaybackDiagnostics();
                }

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
            StopAllSoundsCore(raiseEvents: true);
        }
    }

    public void StopSound(Guid soundId)
    {
        lock (lifecycleLock)
        {
            ThrowIfDisposed();
            StopSoundCore(soundId, raiseEvent: true);
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
        string renderEndpointId,
        AudioMonitorConfiguration monitorConfiguration)
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

            if (AudioDeviceService.IsLikelyVbCableDevice(microphoneDevice))
            {
                throw new InvalidOperationException(
                    $"\"{microphoneDevice.FriendlyName}\" is the Soundboard "
                    + "virtual microphone and cannot be used as the physical "
                    + "microphone input because that would create a feedback loop.");
            }

            if (!AudioDeviceService.IsLikelyVbCableDevice(renderDevice))
            {
                throw new InvalidOperationException(
                    $"\"{renderDevice.FriendlyName}\" is a physical or "
                    + "unrecognized render endpoint. Soundboard only sends "
                    + "combined audio to a likely VB-CABLE endpoint to "
                    + "prevent loud microphone feedback.");
            }

            var relatedCapture = FindRelatedVbCableCapture(
                enumerator,
                renderDevice)
                ?? throw new InvalidOperationException(
                    "No active VB-CABLE capture endpoint such as "
                    + "\"CABLE Output\" was detected. VB-CABLE installation "
                    + "or the required Windows restart appears incomplete.");

            var renderMixFormat = renderDevice.AudioClient.MixFormat;

            if (renderMixFormat.Channels is < 1 or > 2)
            {
                throw new NotSupportedException(
                    $"The selected output exposes {renderMixFormat.Channels} "
                    + "channels. Soundboard supports mono and stereo "
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

            var microphoneMeter = new MeteringSampleProvider(
                microphoneSamples,
                GetSamplesPerNotification(targetFormat));

            var mixer = new MixingSampleProvider(targetFormat)
            {
                ReadFully = true
            };
            mixer.AddMixerInput(microphoneMeter);
            mixer.MixerInputEnded += Mixer_MixerInputEnded;

            var outputSanitizer = new SampleBoundarySanitizer(mixer);
            var outputMeter = new MeteringSampleProvider(
                outputSanitizer,
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
                MicrophoneBufferCapacity)
            {
                MonitoringEnabled = monitorConfiguration.Enabled,
                MonitorEndpointId = monitorConfiguration.EndpointId,
                MonitorInitializationStatus = monitorConfiguration.Enabled
                    ? "Not initialized"
                    : "Disabled by setting"
            };

            var pipeline = new AudioPipelineResources(
                microphoneDevice,
                renderDevice,
                capture,
                output,
                microphoneBuffer,
                microphoneMeter,
                mixer,
                outputSanitizer,
                outputMeter,
                targetFormat,
                pipelineDiagnostics);

            if (monitorConfiguration.Enabled)
            {
                if (string.IsNullOrWhiteSpace(monitorConfiguration.EndpointId))
                {
                    pipeline.Diagnostics = pipeline.Diagnostics with
                    {
                        MonitorInitializationStatus = "Unavailable",
                        LastMonitorWarningOrError =
                            "Sound monitoring was requested, but no physical "
                            + "monitor output is selected. The virtual "
                            + "microphone will continue without monitoring."
                    };
                }
                else
                {
                    try
                    {
                        pipeline.Monitor = CreateMonitorPipeline(
                            enumerator,
                            monitorConfiguration.EndpointId,
                            renderDevice.ID);
                        pipeline.Diagnostics = pipeline.Diagnostics with
                        {
                            MonitorFriendlyName =
                                pipeline.Monitor.Device.FriendlyName,
                            MonitorEndpointId = pipeline.Monitor.Device.ID,
                            MonitorMixFormat =
                                AudioFormatInfo.FromWaveFormat(
                                    pipeline.Monitor.MixFormat),
                            MonitorTargetFormat =
                                AudioFormatInfo.FromWaveFormat(
                                    pipeline.Monitor.TargetFormat),
                            MonitorInitializationStatus = "Ready"
                        };
                    }
                    catch (Exception exception)
                    {
                        pipeline.Diagnostics = pipeline.Diagnostics with
                        {
                            MonitorInitializationStatus = "Unavailable",
                            LastMonitorWarningOrError =
                                "Sound monitoring could not be initialized "
                                + "and was disabled for this engine session. "
                                + "The virtual microphone remains available: "
                                + exception.Message
                        };
                    }
                }
            }

            microphoneDevice = null;
            renderDevice = null;
            capture = null;
            output = null;

            pipeline.Capture.DataAvailable += Capture_DataAvailable;
            pipeline.Capture.RecordingStopped += Capture_RecordingStopped;
            pipeline.Output.PlaybackStopped += Output_PlaybackStopped;
            microphoneMeter.StreamVolume += MicrophoneMeter_StreamVolume;
            outputMeter.StreamVolume += OutputMeter_StreamVolume;
            if (pipeline.Monitor is not null)
            {
                pipeline.Monitor.Output.PlaybackStopped +=
                    MonitorOutput_PlaybackStopped;
                pipeline.Monitor.Mixer.MixerInputEnded +=
                    MonitorMixer_MixerInputEnded;
                pipeline.Monitor.OutputMeter.StreamVolume +=
                    MonitorOutputMeter_StreamVolume;
            }

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

    private MonitorPipelineResources CreateMonitorPipeline(
        MMDeviceEnumerator enumerator,
        string monitorEndpointId,
        string virtualRenderEndpointId)
    {
        MMDevice? monitorDevice = null;
        WasapiOut? monitorOutput = null;

        try
        {
            if (string.Equals(
                    monitorEndpointId,
                    virtualRenderEndpointId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The monitor output cannot be the same endpoint as the "
                    + "VB-CABLE virtual output.");
            }

            monitorDevice = enumerator.GetDevice(monitorEndpointId);
            ValidateEndpoint(
                monitorDevice,
                DataFlow.Render,
                "monitor output");

            if (AudioDeviceService.IsLikelyVbCableDevice(monitorDevice))
            {
                throw new InvalidOperationException(
                    $"\"{monitorDevice.FriendlyName}\" appears to be a "
                    + "virtual cable. Monitoring is restricted to physical "
                    + "headphones or speakers and cannot be overridden.");
            }

            var monitorMixFormat = monitorDevice.AudioClient.MixFormat;
            if (monitorMixFormat.Channels is < 1 or > 2)
            {
                throw new NotSupportedException(
                    $"The selected monitor output exposes "
                    + $"{monitorMixFormat.Channels} channels. Monitoring "
                    + "supports mono and stereo outputs only.");
            }

            var monitorTargetFormat =
                WaveFormat.CreateIeeeFloatWaveFormat(
                    monitorMixFormat.SampleRate,
                    monitorMixFormat.Channels);
            var monitorMixer = new MixingSampleProvider(monitorTargetFormat)
            {
                ReadFully = true
            };
            var monitorSanitizer = new SampleBoundarySanitizer(monitorMixer);
            var monitorMeter = new MeteringSampleProvider(
                monitorSanitizer,
                GetSamplesPerNotification(monitorTargetFormat));

            monitorOutput = new WasapiOut(
                monitorDevice,
                AudioClientShareMode.Shared,
                useEventSync: true,
                RenderLatencyMilliseconds);
            monitorOutput.Init(monitorMeter.ToWaveProvider());

            var pipeline = new MonitorPipelineResources(
                monitorDevice,
                monitorOutput,
                monitorMixer,
                monitorSanitizer,
                monitorMeter,
                monitorMixFormat,
                monitorTargetFormat);
            monitorDevice = null;
            monitorOutput = null;
            return pipeline;
        }
        catch
        {
            DisposeWithoutThrow(monitorOutput);
            DisposeWithoutThrow(monitorDevice);
            throw;
        }
    }

    private static (string FriendlyName, string DeviceId)?
        FindRelatedVbCableCapture(
            MMDeviceEnumerator enumerator,
            MMDevice selectedRenderDevice)
    {
        var selectedControllerId =
            AudioDeviceService.GetControllerDeviceId(selectedRenderDevice);
        var candidates = new List<(
            string FriendlyName,
            string DeviceId,
            string? ControllerDeviceId)>();
        var devices = enumerator.EnumerateAudioEndPoints(
            DataFlow.Capture,
            DeviceState.Active);

        for (var index = 0; index < devices.Count; index++)
        {
            using var device = devices[index];

            if (AudioDeviceService.IsLikelyVbCableDevice(device))
            {
                candidates.Add((
                    device.FriendlyName,
                    device.ID,
                    AudioDeviceService.GetControllerDeviceId(device)));
            }
        }

        var related = !string.IsNullOrWhiteSpace(selectedControllerId)
            ? candidates
                .Where(
                    candidate => string.Equals(
                        candidate.ControllerDeviceId,
                        selectedControllerId,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
        if (related.Length == 1)
        {
            return (related[0].FriendlyName, related[0].DeviceId);
        }

        if (related.Length > 1)
        {
            var preferred = related
                .Where(
                    candidate => candidate.FriendlyName.Contains(
                        "CABLE Output",
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.DeviceId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(preferred.DeviceId))
            {
                return (preferred.FriendlyName, preferred.DeviceId);
            }

            throw new InvalidOperationException(
                "Multiple VB-CABLE capture endpoints belong to the selected "
                + "driver instance, but no primary capture endpoint could be "
                + "identified. Select the standard VB-CABLE render endpoint.");
        }

        if (candidates.Count == 1)
        {
            return (candidates[0].FriendlyName, candidates[0].DeviceId);
        }

        if (candidates.Count > 1)
        {
            throw new InvalidOperationException(
                "Multiple VB-CABLE capture endpoints are active, and none "
                + "could be matched to the selected render endpoint by driver "
                + "instance metadata.");
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
        var pipeline = Volatile.Read(ref resources);
        if (pipeline is null
            || !ReferenceEquals(sender, pipeline.Capture)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            if (eventArgs.BytesRecorded <= 0)
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
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void Capture_RecordingStopped(
        object? sender,
        StoppedEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline is null
            || !ReferenceEquals(sender, pipeline.Capture)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            if (State is AudioEngineState.Stopping or AudioEngineState.Stopped)
            {
                return;
            }

            var details = eventArgs.Exception?.Message
                ?? "the capture endpoint stopped unexpectedly";
            HandleRuntimeFault($"Microphone capture stopped: {details}.");
        }
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void Output_PlaybackStopped(
        object? sender,
        StoppedEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline is null
            || !ReferenceEquals(sender, pipeline.Output)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            if (State is AudioEngineState.Stopping or AudioEngineState.Stopped)
            {
                return;
            }

            var details = eventArgs.Exception?.Message
                ?? "the render endpoint stopped unexpectedly";
            HandleRuntimeFault($"Audio output stopped: {details}.");
        }
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void MonitorOutput_PlaybackStopped(
        object? sender,
        StoppedEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline?.Monitor is not { } monitor
            || !ReferenceEquals(sender, monitor.Output)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            var state = State;
            if (state is AudioEngineState.Stopping
                or AudioEngineState.Stopped
                or AudioEngineState.Faulted)
            {
                return;
            }

            var details = eventArgs.Exception?.Message
                ?? "the physical monitor output stopped unexpectedly";
            QueueMonitorFault(
                pipeline,
                $"Sound monitoring stopped and was disabled for this engine "
                + $"session: {details}. The virtual microphone remains active.");
        }
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void MicrophoneMeter_StreamVolume(
        object? sender,
        StreamVolumeEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline is null
            || !ReferenceEquals(sender, pipeline.MicrophoneMeter)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

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
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void OutputMeter_StreamVolume(
        object? sender,
        StreamVolumeEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline is null
            || !ReferenceEquals(sender, pipeline.OutputMeter)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            Volatile.Write(
                ref mixedOutputPeak,
                GetPeak(eventArgs.MaxSampleValues));
            UpdateBoundaryDiagnostics(pipeline);

            RaisePeakLevelsChanged();
        }
        catch (Exception exception)
        {
            ReportError(
                $"Output metering failed: {exception.Message}",
                isRecoverable: true);
        }
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void MonitorOutputMeter_StreamVolume(
        object? sender,
        StreamVolumeEventArgs eventArgs)
    {
        var pipeline = Volatile.Read(ref resources);
        if (pipeline?.Monitor is not { } monitor
            || !ReferenceEquals(sender, monitor.OutputMeter)
            || !pipeline.TryEnterCallback())
        {
            return;
        }

        try
        {
            var peak = GetPeak(eventArgs.MaxSampleValues);
            Volatile.Write(ref monitorOutputPeak, peak);
            pipeline.Diagnostics = pipeline.Diagnostics with
            {
                MonitorPeak = peak
            };
            UpdateBoundaryDiagnostics(pipeline);

            RaisePeakLevelsChanged();
        }
        catch (Exception exception)
        {
            QueueMonitorFault(
                pipeline,
                "Sound monitoring metering failed and monitoring was "
                + $"disabled: {exception.Message}. The virtual microphone "
                + "remains active.");
        }
        finally
        {
            pipeline.ExitCallback();
        }
    }

    private void Mixer_MixerInputEnded(
        object? sender,
        SampleProviderEventArgs eventArgs)
    {
        if (eventArgs.SampleProvider is not SoundPlaybackBranch branch
            || branch.Role != SoundPlaybackBranchRole.VirtualOutput)
        {
            return;
        }

        SoundPlaybackSession? session;
        lock (lifecycleLock)
        {
            session = activeSounds.Values.FirstOrDefault(
                candidate => ReferenceEquals(candidate.VirtualBranch, branch));
        }
        if (session is null || !session.TryQueueAuthoritativeCompletion())
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ => OnSoundPlaybackCompleted(
                session,
                branch.PlaybackError));
    }

    private void MonitorMixer_MixerInputEnded(
        object? sender,
        SampleProviderEventArgs eventArgs)
    {
        if (eventArgs.SampleProvider is not SoundPlaybackBranch branch
            || branch.Role != SoundPlaybackBranchRole.MonitorOutput)
        {
            return;
        }

        SoundPlaybackSession? session;
        lock (lifecycleLock)
        {
            session = activeSounds.Values.FirstOrDefault(
                candidate => ReferenceEquals(candidate.MonitorBranch, branch));
        }
        if (session is null || !session.TryQueueMonitorCompletion())
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ => OnMonitorBranchCompleted(
                session,
                branch,
                branch.PlaybackError));
    }

    private static float GetPeak(IReadOnlyList<float> values)
    {
        var peak = 0f;

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (!float.IsFinite(value))
            {
                continue;
            }

            peak = Math.Max(peak, Math.Abs(value));
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private void OnSoundPlaybackCompleted(
        SoundPlaybackSession session,
        Exception? exception)
    {
        lock (lifecycleLock)
        {
            if (!activeSounds.TryGetValue(session.SoundId, out var active)
                || !ReferenceEquals(active, session))
            {
                return;
            }

            resources?.Mixer.RemoveMixerInput(session.VirtualBranch);
            var monitorBranch = session.DetachMonitorBranch();
            if (monitorBranch is not null)
            {
                resources?.Monitor?.Mixer.RemoveMixerInput(monitorBranch);
                monitorBranch.Dispose();
            }

            activeSounds.Remove(session.SoundId);
            if (mostRecentSoundId == session.SoundId)
            {
                mostRecentSoundId = activeSounds.Count == 0
                    ? null
                    : activeSounds.Values.MaxBy(item => item.SessionId)?.SoundId;
            }
            session.Dispose();
            ClearPlaybackDiagnostics();
            RaiseSoundPlaybackStateChanged(
                SoundPlaybackChangeReason.Completed,
                session);
        }

        if (exception is not null)
        {
            ReportError(
                $"Sound playback stopped because the file could not be "
                + $"decoded: {exception.Message}",
                isRecoverable: true);
        }
    }

    private void OnMonitorBranchCompleted(
        SoundPlaybackSession session,
        SoundPlaybackBranch branch,
        Exception? exception)
    {
        lock (lifecycleLock)
        {
            if (!activeSounds.TryGetValue(session.SoundId, out var active)
                || !ReferenceEquals(active, session)
                || !ReferenceEquals(session.MonitorBranch, branch))
            {
                return;
            }

            resources?.Monitor?.Mixer.RemoveMixerInput(branch);
            session.DetachMonitorBranch();
            branch.Dispose();
            Volatile.Write(ref monitorOutputPeak, 0f);

            if (exception is null)
            {
                var currentDiagnostics = diagnostics;
                if (currentDiagnostics is not null)
                {
                    diagnostics = currentDiagnostics with
                    {
                        MonitorPeak = 0f
                    };
                }
            }
            else if (resources is not null)
            {
                DisableMonitorPipelineCore(
                    resources,
                    "Sound monitoring stopped because its file branch could "
                    + $"not be decoded: {exception.Message}. Monitoring was "
                    + "disabled for this engine session; the virtual "
                    + "microphone remains active.");
            }
        }

        RaisePeakLevelsChanged();
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

        StopAllSoundsCore(raiseEvents: true);

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

    private void StopAllSoundsCore(bool raiseEvents)
    {
        foreach (var soundId in activeSounds.Keys.ToArray())
        {
            StopSoundCore(soundId, raiseEvents);
        }
    }

    private void StopSoundCore(Guid soundId, bool raiseEvent)
    {
        if (!activeSounds.Remove(soundId, out var session))
        {
            return;
        }

        if (mostRecentSoundId == soundId)
        {
            mostRecentSoundId = activeSounds.Count == 0
                ? null
                : activeSounds.Values.MaxBy(item => item.SessionId)?.SoundId;
        }

        resources?.Mixer.RemoveMixerInput(session.VirtualBranch);
        var monitorBranch = session.DetachMonitorBranch();
        if (monitorBranch is not null)
        {
            resources?.Monitor?.Mixer.RemoveMixerInput(monitorBranch);
            monitorBranch.Dispose();
        }

        session.Dispose();
        Volatile.Write(ref monitorOutputPeak, 0f);
        ClearPlaybackDiagnostics();

        if (raiseEvent)
        {
            RaiseSoundPlaybackStateChanged(
                SoundPlaybackChangeReason.Stopped,
                session);
        }
    }

    private void QueueMonitorFault(
        AudioPipelineResources sourcePipeline,
        string message)
    {
        if (Interlocked.Exchange(ref monitorFaultCleanupQueued, 1) != 0)
        {
            return;
        }

        ThreadPool.QueueUserWorkItem(
            _ =>
            {
                try
                {
                    lock (lifecycleLock)
                    {
                        var pipeline = resources;
                        if (!ReferenceEquals(pipeline, sourcePipeline)
                            || State != AudioEngineState.Running)
                        {
                            return;
                        }

                        DisableMonitorPipelineCore(pipeline, message);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref monitorFaultCleanupQueued, 0);
                }
            });
    }

    private void DisableMonitorPipelineCore(
        AudioPipelineResources pipeline,
        string message,
        bool reportError = true)
    {
        var monitor = pipeline.Monitor;
        if (monitor is null)
        {
            return;
        }

        pipeline.Monitor = null;
        monitor.Output.PlaybackStopped -= MonitorOutput_PlaybackStopped;
        monitor.Mixer.MixerInputEnded -= MonitorMixer_MixerInputEnded;
        monitor.OutputMeter.StreamVolume -=
            MonitorOutputMeter_StreamVolume;

        foreach (var session in activeSounds.Values)
        {
            var monitorBranch = session.DetachMonitorBranch();
            if (monitorBranch is not null)
            {
                monitor.Mixer.RemoveMixerInput(monitorBranch);
                monitorBranch.Dispose();
            }
        }

        monitor.Dispose();
        Volatile.Write(ref monitorOutputPeak, 0f);
        pipeline.Diagnostics = pipeline.Diagnostics with
        {
            MonitorInitializationStatus = "Disabled after failure",
            MonitorPeak = 0f,
            MonitorResamplingActive = null,
            MonitorChannelConversionActive = null,
            LastMonitorWarningOrError = message
        };
        diagnostics = pipeline.Diagnostics;
        RaisePeakLevelsChanged();
        if (reportError)
        {
            ReportError(message, isRecoverable: true);
        }
    }

    private void UpdatePlaybackDiagnostics(SoundPlaybackSession session)
    {
        if (resources is null)
        {
            return;
        }

        var monitorBranch = session.MonitorBranch;
        resources.Diagnostics = resources.Diagnostics with
        {
            CurrentSoundId = session.SoundId,
            CurrentPlaybackSessionId = session.SessionId,
            MonitorResamplingActive = monitorBranch?.ResamplingActive,
            MonitorChannelConversionActive =
                monitorBranch?.ChannelConversionActive,
            ActiveSoundCount = activeSounds.Count
        };
        diagnostics = resources.Diagnostics;
    }

    private void UpdateBoundaryDiagnostics(AudioPipelineResources pipeline)
    {
        var monitorSanitizer = pipeline.Monitor?.Sanitizer;
        pipeline.Diagnostics = pipeline.Diagnostics with
        {
            ClippedSampleCount = pipeline.OutputSanitizer.ClippedSampleCount
                + (monitorSanitizer?.ClippedSampleCount ?? 0),
            NonFiniteSampleCount =
                pipeline.OutputSanitizer.NonFiniteSampleCount
                + (monitorSanitizer?.NonFiniteSampleCount ?? 0)
        };
        diagnostics = pipeline.Diagnostics;
    }

    private void ClearPlaybackDiagnostics()
    {
        if (resources is null)
        {
            return;
        }

        var latest = activeSounds.Values.MaxBy(item => item.SessionId);
        resources.Diagnostics = resources.Diagnostics with
        {
            CurrentSoundId = latest?.SoundId,
            CurrentPlaybackSessionId = latest?.SessionId,
            MonitorResamplingActive = latest?.MonitorBranch?.ResamplingActive,
            MonitorChannelConversionActive =
                latest?.MonitorBranch?.ChannelConversionActive,
            ActiveSoundCount = activeSounds.Count,
            MonitorPeak = 0f
        };
        diagnostics = resources.Diagnostics;
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
        Volatile.Write(ref monitorOutputPeak, 0f);
        var currentDiagnostics = diagnostics;
        if (currentDiagnostics is not null)
        {
            diagnostics = currentDiagnostics with
            {
                MonitorPeak = 0f
            };
        }

        RaisePeakLevelsChanged();
    }

    private void RaisePeakLevelsChanged()
    {
        PeakLevelsChanged?.Invoke(
            this,
            new AudioPeakLevelsEventArgs(
                Volatile.Read(ref microphonePeak),
                Volatile.Read(ref mixedOutputPeak),
                Volatile.Read(ref monitorOutputPeak)));
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
        SoundPlaybackChangeReason reason,
        SoundPlaybackSession session)
    {
        SoundPlaybackStateChanged?.Invoke(
            this,
            new SoundPlaybackStateChangedEventArgs(
                reason,
                session.SoundId,
                session.SessionId));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static void ValidateVolume(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Volume gain must be between silence (0.0) and unity (1.0).");
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
        if (pipeline.Monitor is not null)
        {
            pipeline.Monitor.Output.PlaybackStopped -=
                MonitorOutput_PlaybackStopped;
            pipeline.Monitor.Mixer.MixerInputEnded -=
                MonitorMixer_MixerInputEnded;
            pipeline.Monitor.OutputMeter.StreamVolume -=
                MonitorOutputMeter_StreamVolume;
        }

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
        private readonly object callbackLock = new();
        private int activeCallbackCount;
        private bool callbacksClosing;
        private bool disposed;

        public AudioPipelineResources(
            MMDevice microphoneDevice,
            MMDevice renderDevice,
            WasapiCapture capture,
            WasapiOut output,
            BufferedWaveProvider microphoneBuffer,
            MeteringSampleProvider microphoneMeter,
            MixingSampleProvider mixer,
            SampleBoundarySanitizer outputSanitizer,
            MeteringSampleProvider outputMeter,
            WaveFormat targetFormat,
            AudioMixEngineDiagnostics diagnostics)
        {
            MicrophoneDevice = microphoneDevice;
            RenderDevice = renderDevice;
            Capture = capture;
            Output = output;
            MicrophoneBuffer = microphoneBuffer;
            MicrophoneMeter = microphoneMeter;
            Mixer = mixer;
            OutputSanitizer = outputSanitizer;
            OutputMeter = outputMeter;
            TargetFormat = targetFormat;
            Diagnostics = diagnostics;
        }

        public MMDevice MicrophoneDevice { get; }

        public MMDevice RenderDevice { get; }

        public WasapiCapture Capture { get; }

        public WasapiOut Output { get; }

        public BufferedWaveProvider MicrophoneBuffer { get; }

        public MeteringSampleProvider MicrophoneMeter { get; }

        public MixingSampleProvider Mixer { get; }

        public SampleBoundarySanitizer OutputSanitizer { get; }

        public MeteringSampleProvider OutputMeter { get; }

        public WaveFormat TargetFormat { get; }

        public AudioMixEngineDiagnostics Diagnostics { get; set; }

        public MonitorPipelineResources? Monitor { get; set; }

        public bool TryEnterCallback()
        {
            lock (callbackLock)
            {
                if (callbacksClosing)
                {
                    return false;
                }

                activeCallbackCount++;
                return true;
            }
        }

        public void ExitCallback()
        {
            lock (callbackLock)
            {
                activeCallbackCount--;
                if (activeCallbackCount == 0)
                {
                    System.Threading.Monitor.PulseAll(callbackLock);
                }
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            lock (callbackLock)
            {
                callbacksClosing = true;
            }

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

            lock (callbackLock)
            {
                while (activeCallbackCount > 0)
                {
                    System.Threading.Monitor.Wait(callbackLock);
                }
            }

            RunCleanupWithoutThrow(MicrophoneBuffer.ClearBuffer);
            RunCleanupWithoutThrow(Mixer.RemoveAllMixerInputs);
            DisposeWithoutThrow(Monitor);
            Monitor = null;
            DisposeWithoutThrow(Capture);
            DisposeWithoutThrow(Output);
            DisposeWithoutThrow(MicrophoneDevice);
            DisposeWithoutThrow(RenderDevice);
        }
    }

    private sealed class MonitorPipelineResources : IDisposable
    {
        private bool disposed;

        public MonitorPipelineResources(
            MMDevice device,
            WasapiOut output,
            MixingSampleProvider mixer,
            SampleBoundarySanitizer sanitizer,
            MeteringSampleProvider outputMeter,
            WaveFormat mixFormat,
            WaveFormat targetFormat)
        {
            Device = device;
            Output = output;
            Mixer = mixer;
            Sanitizer = sanitizer;
            OutputMeter = outputMeter;
            MixFormat = mixFormat;
            TargetFormat = targetFormat;
        }

        public MMDevice Device { get; }

        public WasapiOut Output { get; }

        public MixingSampleProvider Mixer { get; }

        public SampleBoundarySanitizer Sanitizer { get; }

        public MeteringSampleProvider OutputMeter { get; }

        public WaveFormat MixFormat { get; }

        public WaveFormat TargetFormat { get; }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;

            try
            {
                Output.Stop();
            }
            catch
            {
                // Device removal can make Stop fail. Dispose still runs.
            }

            RunCleanupWithoutThrow(Mixer.RemoveAllMixerInputs);
            DisposeWithoutThrow(Output);
            DisposeWithoutThrow(Device);
        }
    }
}
