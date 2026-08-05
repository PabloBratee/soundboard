using NAudio.Wave;

namespace Soundboard.Audio;

/// <summary>
/// How easily speech engages Voice Priority. Values are engage thresholds
/// expressed in dBFS against a short-window microphone RMS.
/// </summary>
public enum VoiceSensitivity
{
    Low,
    Normal,
    High
}

/// <summary>
/// How far sounds are lowered while speech is detected.
/// </summary>
public enum VoiceDuckingStrength
{
    Light,
    Balanced,
    Strong
}

/// <summary>
/// User-facing Voice Priority configuration. Disabled by default so an
/// existing installation keeps its previous audio behavior after an update.
/// </summary>
public sealed record VoicePrioritySettings(
    bool Enabled,
    VoiceSensitivity Sensitivity,
    VoiceDuckingStrength Strength)
{
    /// <summary>
    /// Distance between the engage and disengage thresholds. The detector
    /// keeps ducking until the microphone falls this far below the engage
    /// level, so speech pauses near the threshold do not toggle the state.
    /// </summary>
    public const double HysteresisDb = 6d;

    public static VoicePrioritySettings Disabled { get; } = new(
        Enabled: false,
        VoiceSensitivity.Normal,
        VoiceDuckingStrength.Balanced);

    public double EngageThresholdDb => Sensitivity switch
    {
        VoiceSensitivity.Low => -30d,
        VoiceSensitivity.High => -42d,
        _ => -36d
    };

    public double DisengageThresholdDb => EngageThresholdDb - HysteresisDb;

    public double DuckingDb => Strength switch
    {
        VoiceDuckingStrength.Light => -6d,
        VoiceDuckingStrength.Strong => -18d,
        _ => -12d
    };

    public float EngageThreshold => DecibelsToLinear(EngageThresholdDb);

    public float DisengageThreshold => DecibelsToLinear(DisengageThresholdDb);

    public float DuckingGain => DecibelsToLinear(DuckingDb);

    /// <summary>gain = 10^(dB / 20)</summary>
    public static float DecibelsToLinear(double decibels)
    {
        return (float)Math.Pow(10d, decibels / 20d);
    }
}

/// <summary>
/// Shared, lock-free state between the microphone activity detector and the
/// ducking gain applied to soundboard playback. One instance lives for the
/// lifetime of the engine so a device reconnect never loses the setting.
/// </summary>
internal sealed class VoicePriorityController
{
    /// <summary>10-90 % transition time while sounds are lowered.</summary>
    public const double AttackMilliseconds = 50d;

    /// <summary>10-90 % transition time while sounds return to normal.</summary>
    public const double ReleaseMilliseconds = 500d;

    /// <summary>
    /// Shortest time the detector stays engaged after the last window that
    /// crossed the engage threshold.
    /// </summary>
    public const double MinimumSpeechHoldMilliseconds = 200d;

    /// <summary>Detector integration window for the microphone RMS.</summary>
    public const double DetectionWindowMilliseconds = 10d;

    private int enabled;
    private int speaking;
    private float engageThreshold =
        VoicePrioritySettings.Disabled.EngageThreshold;
    private float disengageThreshold =
        VoicePrioritySettings.Disabled.DisengageThreshold;
    private float duckingGain =
        VoicePrioritySettings.Disabled.DuckingGain;

    public bool Enabled => Volatile.Read(ref enabled) != 0;

    public bool IsDucking => Enabled && Volatile.Read(ref speaking) != 0;

    public float EngageThreshold => Volatile.Read(ref engageThreshold);

    public float DisengageThreshold => Volatile.Read(ref disengageThreshold);

    /// <summary>
    /// The gain soundboard playback is currently heading towards. Unity
    /// whenever Voice Priority is off or no speech is detected.
    /// </summary>
    public float TargetGain =>
        IsDucking ? Volatile.Read(ref duckingGain) : 1f;

    public void Apply(VoicePrioritySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Volatile.Write(ref engageThreshold, settings.EngageThreshold);
        Volatile.Write(ref disengageThreshold, settings.DisengageThreshold);
        Volatile.Write(ref duckingGain, settings.DuckingGain);
        Volatile.Write(ref enabled, settings.Enabled ? 1 : 0);

        if (!settings.Enabled)
        {
            Volatile.Write(ref speaking, 0);
        }
    }

    public void SetSpeaking(bool value)
    {
        Volatile.Write(ref speaking, value ? 1 : 0);
    }

    public void Reset()
    {
        Volatile.Write(ref speaking, 0);
    }
}

/// <summary>
/// Passes the physical microphone branch through untouched and measures it
/// before it reaches the soundboard mix, so soundboard playback can never
/// trigger its own ducking. Allocation-free and driven only by the render
/// pull, never by a UI timer.
/// </summary>
internal sealed class MicrophoneActivityDetector : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly VoicePriorityController controller;
    private readonly int windowSampleCount;
    private readonly int holdSampleCount;
    private double squareSum;
    private int windowSamples;
    private int holdSamplesRemaining;
    private bool speaking;

    public MicrophoneActivityDetector(
        ISampleProvider source,
        VoicePriorityController controller)
    {
        this.source = source
            ?? throw new ArgumentNullException(nameof(source));
        this.controller = controller
            ?? throw new ArgumentNullException(nameof(controller));

        var samplesPerSecond =
            source.WaveFormat.SampleRate * source.WaveFormat.Channels;
        windowSampleCount = Math.Max(
            source.WaveFormat.Channels,
            (int)(samplesPerSecond
                * VoicePriorityController.DetectionWindowMilliseconds
                / 1000d));
        holdSampleCount = Math.Max(
            windowSampleCount,
            (int)(samplesPerSecond
                * VoicePriorityController.MinimumSpeechHoldMilliseconds
                / 1000d));
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);

        if (!controller.Enabled)
        {
            if (speaking || windowSamples > 0)
            {
                ResetDetection();
            }

            return read;
        }

        for (var index = 0; index < read; index++)
        {
            var sample = buffer[offset + index];
            if (float.IsFinite(sample))
            {
                squareSum += (double)sample * sample;
            }

            windowSamples++;
            if (windowSamples >= windowSampleCount)
            {
                EvaluateWindow();
            }
        }

        return read;
    }

    private void EvaluateWindow()
    {
        var rms = (float)Math.Sqrt(squareSum / windowSamples);
        var elapsed = windowSamples;
        squareSum = 0d;
        windowSamples = 0;

        if (speaking)
        {
            if (rms >= controller.DisengageThreshold)
            {
                if (rms >= controller.EngageThreshold)
                {
                    holdSamplesRemaining = holdSampleCount;
                }

                return;
            }

            holdSamplesRemaining -= elapsed;
            if (holdSamplesRemaining > 0)
            {
                return;
            }

            speaking = false;
            holdSamplesRemaining = 0;
            controller.SetSpeaking(false);
            return;
        }

        if (rms < controller.EngageThreshold)
        {
            return;
        }

        speaking = true;
        holdSamplesRemaining = holdSampleCount;
        controller.SetSpeaking(true);
    }

    private void ResetDetection()
    {
        squareSum = 0d;
        windowSamples = 0;
        holdSamplesRemaining = 0;
        speaking = false;
        controller.SetSpeaking(false);
    }
}

/// <summary>
/// Applies the Voice Priority gain to decoded soundboard audio only. The
/// transition is sample-rate aware and exponential, so no click, step, or
/// zipper noise is produced. The microphone branch never passes through it.
/// </summary>
internal sealed class VoiceDuckingSampleProvider : ISampleProvider
{
    private const float SnapThreshold = 0.0005f;

    private readonly ISampleProvider source;
    private readonly VoicePriorityController controller;
    private readonly float attackCoefficient;
    private readonly float releaseCoefficient;
    private float currentGain = 1f;

    public VoiceDuckingSampleProvider(
        ISampleProvider source,
        VoicePriorityController controller)
    {
        this.source = source
            ?? throw new ArgumentNullException(nameof(source));
        this.controller = controller
            ?? throw new ArgumentNullException(nameof(controller));
        attackCoefficient = CreateCoefficient(
            source.WaveFormat.SampleRate,
            VoicePriorityController.AttackMilliseconds);
        releaseCoefficient = CreateCoefficient(
            source.WaveFormat.SampleRate,
            VoicePriorityController.ReleaseMilliseconds);
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    public float CurrentGain => currentGain;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = source.Read(buffer, offset, count);
        var target = controller.TargetGain;

        // Unity in and unity out: the samples are left bit-for-bit untouched
        // whenever Voice Priority is idle or switched off. The smoother never
        // overshoots, so a current gain of one means exactly unity.
        if (read <= 0 || (currentGain >= 1f && target >= 1f))
        {
            return read;
        }

        var channels = WaveFormat.Channels;
        var completeFrameSamples = read - (read % channels);

        for (var sampleOffset = 0;
             sampleOffset < completeFrameSamples;
             sampleOffset += channels)
        {
            AdvanceGain(target);
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

    private void AdvanceGain(float target)
    {
        if (currentGain == target)
        {
            return;
        }

        var coefficient = currentGain > target
            ? attackCoefficient
            : releaseCoefficient;
        currentGain = target + ((currentGain - target) * coefficient);

        if (Math.Abs(currentGain - target) < SnapThreshold)
        {
            currentGain = target;
        }
    }

    /// <summary>
    /// Per-frame coefficient of a one-pole smoother whose 10-90 % transition
    /// takes the requested time at the given sample rate.
    /// </summary>
    private static float CreateCoefficient(
        int sampleRate,
        double milliseconds)
    {
        var frames = Math.Max(1d, sampleRate * milliseconds / 1000d);
        return (float)Math.Exp(-2.2d / frames);
    }
}
