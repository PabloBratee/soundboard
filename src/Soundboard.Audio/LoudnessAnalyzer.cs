using NAudio.Wave;

namespace Soundboard.Audio;

public sealed class LoudnessAnalyzer
{
    public const int AlgorithmVersion = 1;
    public const double MinimumReportedDbfs = -200d;

    private const double LoudnessOffset = -0.691d;
    private const double AbsoluteGateLufs = -70d;
    private const double RelativeGateLu = -10d;
    private static readonly TimeSpan BlockDuration =
        TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan BlockHop =
        TimeSpan.FromMilliseconds(100);

    private readonly IAudioFileDecoderFactory decoderFactory;

    public LoudnessAnalyzer(
        IAudioFileDecoderFactory? decoderFactory = null)
    {
        this.decoderFactory =
            decoderFactory ?? AudioFileDecoderFactory.Default;
    }

    public LoudnessAnalysisResult AnalyzeFile(
        string filePath,
        AudioClipSettings clipSettings,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(clipSettings);
        if (!File.Exists(filePath))
        {
            return LoudnessAnalysisResult.Invalid(
                "The managed audio file is missing.",
                clipSettings.EffectiveDuration);
        }

        try
        {
            using var decoded = decoderFactory.Open(filePath);
            if (decoded.ChannelCount is < 1 or > 2)
            {
                return LoudnessAnalysisResult.Invalid(
                    $"The decoded source has {decoded.ChannelCount} channels; "
                    + "loudness analysis supports mono and stereo only.",
                    clipSettings.EffectiveDuration);
            }

            if (decoded.Duration != clipSettings.SourceDuration)
            {
                return LoudnessAnalysisResult.Invalid(
                    "The decoded duration no longer matches the library metadata.",
                    clipSettings.EffectiveDuration);
            }

            var clipped = new AudioClipSampleProvider(
                decoded.SampleProvider,
                clipSettings);
            return AnalyzeEffectiveClip(
                clipped,
                clipSettings.EffectiveDuration,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return LoudnessAnalysisResult.Invalid(
                $"Loudness analysis failed: {exception.Message}",
                clipSettings.EffectiveDuration);
        }
    }

    public LoudnessAnalysisResult AnalyzeEffectiveClip(
        ISampleProvider source,
        TimeSpan effectiveDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var channels = source.WaveFormat.Channels;
        var sampleRate = source.WaveFormat.SampleRate;
        if (channels is < 1 or > 2)
        {
            return LoudnessAnalysisResult.Invalid(
                $"The decoded source has {channels} channels; loudness "
                + "analysis supports mono and stereo only.",
                effectiveDuration);
        }

        if (effectiveDuration <= TimeSpan.Zero)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip is empty.",
                effectiveDuration);
        }

        if (effectiveDuration < BlockDuration)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip is shorter than the 400 ms analysis window.",
                effectiveDuration);
        }

        var blockFrames = Math.Max(
            1,
            (int)Math.Round(BlockDuration.TotalSeconds * sampleRate));
        var hopFrames = Math.Max(
            1,
            (int)Math.Round(BlockHop.TotalSeconds * sampleRate));
        var energyRing = new double[blockFrames];
        var states = new ChannelFilterState[channels];
        for (var channel = 0; channel < channels; channel++)
        {
            states[channel] = new ChannelFilterState(sampleRate);
        }

        var blockEnergies = new List<double>();
        var buffer = new float[8192 - (8192 % channels)];
        long framesRead = 0;
        var ringIndex = 0;
        var runningEnergy = 0d;
        var maximumPeak = 0d;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var alignedRead = read - (read % channels);
            for (var index = 0; index < alignedRead; index += channels)
            {
                var frameEnergy = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = buffer[index + channel];
                    if (!float.IsFinite(sample))
                    {
                        return LoudnessAnalysisResult.Invalid(
                            "The decoded clip contains a non-finite audio sample.",
                            effectiveDuration,
                            PeakToDbfs(maximumPeak));
                    }

                    maximumPeak = Math.Max(maximumPeak, Math.Abs(sample));
                    var weighted = states[channel].Process(sample);
                    frameEnergy += weighted * weighted;
                }

                runningEnergy -= energyRing[ringIndex];
                energyRing[ringIndex] = frameEnergy;
                runningEnergy += frameEnergy;
                ringIndex++;
                if (ringIndex == blockFrames)
                {
                    ringIndex = 0;
                }

                framesRead++;
                if (framesRead >= blockFrames
                    && (framesRead - blockFrames) % hopFrames == 0)
                {
                    blockEnergies.Add(
                        Math.Max(0d, runningEnergy / blockFrames));
                }
            }
        }

        var actualDuration = TimeSpan.FromSeconds(
            framesRead / (double)sampleRate);
        if (framesRead == 0)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip contains no decoded sample frames.",
                actualDuration);
        }

        if (blockEnergies.Count == 0)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip is shorter than the useful analysis window.",
                actualDuration,
                PeakToDbfs(maximumPeak));
        }

        if (maximumPeak <= 0d)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip is digital silence and cannot be normalized.",
                actualDuration);
        }

        var absoluteGated = blockEnergies
            .Where(
                energy =>
                    energy > 0d
                    && EnergyToLufs(energy) >= AbsoluteGateLufs)
            .ToArray();
        if (absoluteGated.Length == 0)
        {
            return LoudnessAnalysisResult.Invalid(
                "The effective clip is silent or too quiet for meaningful "
                + "loudness normalization.",
                actualDuration,
                PeakToDbfs(maximumPeak));
        }

        var preliminaryLoudness = EnergyToLufs(absoluteGated.Average());
        var relativeThreshold = preliminaryLoudness + RelativeGateLu;
        var relativeGated = absoluteGated
            .Where(energy => EnergyToLufs(energy) >= relativeThreshold)
            .ToArray();
        if (relativeGated.Length == 0)
        {
            return LoudnessAnalysisResult.Invalid(
                "No analysis blocks remained after loudness gating.",
                actualDuration,
                PeakToDbfs(maximumPeak));
        }

        var integrated = EnergyToLufs(relativeGated.Average());
        var peakDbfs = PeakToDbfs(maximumPeak);
        if (!double.IsFinite(integrated) || !double.IsFinite(peakDbfs))
        {
            return LoudnessAnalysisResult.Invalid(
                "Loudness analysis produced a non-finite result.",
                actualDuration);
        }

        return new LoudnessAnalysisResult(
            integrated,
            peakDbfs,
            actualDuration.TotalSeconds,
            AlgorithmVersion,
            IsValid: true,
            InvalidReason: null);
    }

    private static double EnergyToLufs(double energy)
    {
        return energy <= 0d
            ? MinimumReportedDbfs
            : LoudnessOffset + 10d * Math.Log10(energy);
    }

    private static double PeakToDbfs(double peak)
    {
        return peak <= 0d
            ? MinimumReportedDbfs
            : Math.Max(MinimumReportedDbfs, 20d * Math.Log10(peak));
    }

    private sealed class ChannelFilterState
    {
        private readonly Biquad highShelf;
        private readonly Biquad highPass;

        public ChannelFilterState(int sampleRate)
        {
            highShelf = Biquad.CreateHighShelf(
                sampleRate,
                1681.974450955533,
                3.999843853973347,
                1d);
            highPass = Biquad.CreateHighPass(
                sampleRate,
                38.13547087602444,
                0.5003270373238773);
        }

        public double Process(double sample)
        {
            return highPass.Process(highShelf.Process(sample));
        }
    }

    private sealed class Biquad
    {
        private readonly double b0;
        private readonly double b1;
        private readonly double b2;
        private readonly double a1;
        private readonly double a2;
        private double x1;
        private double x2;
        private double y1;
        private double y2;

        private Biquad(
            double b0,
            double b1,
            double b2,
            double a0,
            double a1,
            double a2)
        {
            this.b0 = b0 / a0;
            this.b1 = b1 / a0;
            this.b2 = b2 / a0;
            this.a1 = a1 / a0;
            this.a2 = a2 / a0;
        }

        public double Process(double sample)
        {
            var output =
                b0 * sample + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1;
            x1 = sample;
            y2 = y1;
            y1 = output;
            return output;
        }

        public static Biquad CreateHighPass(
            int sampleRate,
            double frequency,
            double quality)
        {
            var omega = 2d * Math.PI * frequency / sampleRate;
            var cosine = Math.Cos(omega);
            var sine = Math.Sin(omega);
            var alpha = sine / (2d * quality);
            return new Biquad(
                (1d + cosine) / 2d,
                -(1d + cosine),
                (1d + cosine) / 2d,
                1d + alpha,
                -2d * cosine,
                1d - alpha);
        }

        public static Biquad CreateHighShelf(
            int sampleRate,
            double frequency,
            double gainDb,
            double slope)
        {
            var amplitude = Math.Pow(10d, gainDb / 40d);
            var omega = 2d * Math.PI * frequency / sampleRate;
            var cosine = Math.Cos(omega);
            var sine = Math.Sin(omega);
            var alpha = sine / 2d * Math.Sqrt(
                (amplitude + 1d / amplitude) * (1d / slope - 1d) + 2d);
            var root = 2d * Math.Sqrt(amplitude) * alpha;
            return new Biquad(
                amplitude
                    * ((amplitude + 1d)
                        + (amplitude - 1d) * cosine
                        + root),
                -2d * amplitude
                    * ((amplitude - 1d)
                        + (amplitude + 1d) * cosine),
                amplitude
                    * ((amplitude + 1d)
                        + (amplitude - 1d) * cosine
                        - root),
                (amplitude + 1d)
                    - (amplitude - 1d) * cosine
                    + root,
                2d
                    * ((amplitude - 1d)
                        - (amplitude + 1d) * cosine),
                (amplitude + 1d)
                    - (amplitude - 1d) * cosine
                    - root);
        }
    }
}
