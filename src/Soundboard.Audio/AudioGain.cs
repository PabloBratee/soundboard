namespace Soundboard.Audio;

/// <summary>
/// Defines the single user-facing volume curve used by broadcast and preview.
/// </summary>
public static class AudioGain
{
    public const double MinimumPercent = 0d;
    public const double MaximumPercent = 100d;

    /// <summary>
    /// Converts a percentage to amplitude using a squared audio taper.
    /// Fifty percent is -12.04 dB; zero is exact digital silence.
    /// </summary>
    public static float FromPercent(double percent)
    {
        if (!double.IsFinite(percent)
            || percent < MinimumPercent
            || percent > MaximumPercent)
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        if (percent == 0d)
        {
            return 0f;
        }

        var normalized = percent / MaximumPercent;
        return (float)(normalized * normalized);
    }

    public static float Combine(double soundPercent, double masterPercent)
    {
        return FromPercent(soundPercent) * FromPercent(masterPercent);
    }
}
