using System.Security.Cryptography;
using System.Text;

namespace Soundboard.Audio;

public sealed record LoudnessAnalysisKey(
    string ContentHash,
    int TrimStartMilliseconds,
    int? TrimEndMilliseconds,
    int FadeInMilliseconds,
    int FadeOutMilliseconds,
    int AlgorithmVersion)
{
    public static LoudnessAnalysisKey Create(
        string contentHash,
        AudioClipSettings clipSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentNullException.ThrowIfNull(clipSettings);

        return new LoudnessAnalysisKey(
            contentHash.Trim().ToUpperInvariant(),
            checked((int)Math.Round(
                clipSettings.TrimStart.TotalMilliseconds)),
            clipSettings.TrimEnd == clipSettings.SourceDuration
                ? null
                : checked((int)Math.Round(
                    clipSettings.TrimEnd.TotalMilliseconds)),
            checked((int)Math.Round(clipSettings.FadeIn.TotalMilliseconds)),
            checked((int)Math.Round(clipSettings.FadeOut.TotalMilliseconds)),
            LoudnessAnalyzer.AlgorithmVersion);
    }

    public string GetStableId()
    {
        var canonical = string.Join(
            "|",
            ContentHash,
            TrimStartMilliseconds,
            TrimEndMilliseconds?.ToString() ?? "FULL",
            FadeInMilliseconds,
            FadeOutMilliseconds,
            AlgorithmVersion);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record LoudnessAnalysisResult(
    double IntegratedLoudnessLufs,
    double MaximumSamplePeakDbfs,
    double EffectiveDurationSeconds,
    int AlgorithmVersion,
    bool IsValid,
    string? InvalidReason)
{
    public static LoudnessAnalysisResult Invalid(
        string reason,
        TimeSpan effectiveDuration,
        double maximumSamplePeakDbfs = LoudnessAnalyzer.MinimumReportedDbfs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new LoudnessAnalysisResult(
            LoudnessAnalyzer.MinimumReportedDbfs,
            double.IsFinite(maximumSamplePeakDbfs)
                ? maximumSamplePeakDbfs
                : LoudnessAnalyzer.MinimumReportedDbfs,
            double.IsFinite(effectiveDuration.TotalSeconds)
                ? Math.Max(0d, effectiveDuration.TotalSeconds)
                : 0d,
            LoudnessAnalyzer.AlgorithmVersion,
            IsValid: false,
            reason);
    }

    public bool HasFiniteValues =>
        double.IsFinite(IntegratedLoudnessLufs)
        && double.IsFinite(MaximumSamplePeakDbfs)
        && double.IsFinite(EffectiveDurationSeconds);
}

public sealed record LoudnessAnalysisOutcome(
    LoudnessAnalysisKey Key,
    LoudnessAnalysisResult Result,
    bool LoadedFromCache,
    string? Warning);

public sealed record LoudnessNormalizationSettings(
    bool Enabled,
    double TargetLufs)
{
    public const double DefaultTargetLufs = -16d;
    public const double MinimumTargetLufs = -24d;
    public const double MaximumTargetLufs = -10d;
    public const double MaximumBoostDb = 12d;
    public const double MaximumAttenuationDb = -24d;
}

public sealed record LoudnessNormalizationCalculation(
    bool IsAvailable,
    double TargetLufs,
    double MeasuredLufs,
    double RequestedGainDb,
    double AppliedGainDb,
    bool WasClamped,
    string? UnavailableReason)
{
    public float LinearGain => IsAvailable
        ? (float)Math.Pow(10d, AppliedGainDb / 20d)
        : 1f;
}

public static class LoudnessNormalization
{
    public static LoudnessNormalizationCalculation Calculate(
        LoudnessAnalysisResult result,
        double targetLufs)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!double.IsFinite(targetLufs)
            || targetLufs < LoudnessNormalizationSettings.MinimumTargetLufs
            || targetLufs > LoudnessNormalizationSettings.MaximumTargetLufs)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetLufs),
                "The loudness target must be between -24 and -10 LUFS.");
        }

        if (!result.IsValid || !result.HasFiniteValues)
        {
            return new LoudnessNormalizationCalculation(
                IsAvailable: false,
                targetLufs,
                LoudnessAnalyzer.MinimumReportedDbfs,
                0d,
                0d,
                WasClamped: false,
                result.InvalidReason ?? "Loudness analysis is invalid.");
        }

        var requested = targetLufs - result.IntegratedLoudnessLufs;
        var applied = Math.Clamp(
            requested,
            LoudnessNormalizationSettings.MaximumAttenuationDb,
            LoudnessNormalizationSettings.MaximumBoostDb);
        return new LoudnessNormalizationCalculation(
            IsAvailable: true,
            targetLufs,
            result.IntegratedLoudnessLufs,
            requested,
            applied,
            Math.Abs(requested - applied) > 0.0001d,
            null);
    }
}
