namespace Soundboard.Audio;

public sealed class AudioClipSettings
{
    public static readonly TimeSpan MinimumPlayableDuration =
        TimeSpan.FromMilliseconds(100);

    private AudioClipSettings(
        TimeSpan sourceDuration,
        TimeSpan trimStart,
        TimeSpan trimEnd,
        TimeSpan fadeIn,
        TimeSpan fadeOut)
    {
        SourceDuration = sourceDuration;
        TrimStart = trimStart;
        TrimEnd = trimEnd;
        FadeIn = fadeIn;
        FadeOut = fadeOut;
    }

    public TimeSpan SourceDuration { get; }

    public TimeSpan TrimStart { get; }

    public TimeSpan TrimEnd { get; }

    public TimeSpan FadeIn { get; }

    public TimeSpan FadeOut { get; }

    public TimeSpan EffectiveDuration => TrimEnd - TrimStart;

    public bool IsEdited =>
        TrimStart > TimeSpan.Zero
        || TrimEnd < SourceDuration
        || FadeIn > TimeSpan.Zero
        || FadeOut > TimeSpan.Zero;

    public static AudioClipSettings FullDuration(TimeSpan sourceDuration)
    {
        ValidateSourceDuration(sourceDuration);
        return new AudioClipSettings(
            sourceDuration,
            TimeSpan.Zero,
            sourceDuration,
            TimeSpan.Zero,
            TimeSpan.Zero);
    }

    public static AudioClipSettings Create(
        TimeSpan sourceDuration,
        int trimStartMilliseconds,
        int? trimEndMilliseconds,
        int fadeInMilliseconds,
        int fadeOutMilliseconds)
    {
        ValidateSourceDuration(sourceDuration);

        if (trimStartMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trimStartMilliseconds),
                "Trim start cannot be negative.");
        }

        if (trimEndMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trimEndMilliseconds),
                "Trim end cannot be negative.");
        }

        if (fadeInMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fadeInMilliseconds),
                "Fade in cannot be negative.");
        }

        if (fadeOutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fadeOutMilliseconds),
                "Fade out cannot be negative.");
        }

        var trimStart = TimeSpan.FromMilliseconds(trimStartMilliseconds);
        var trimEnd = trimEndMilliseconds is { } endMilliseconds
            ? TimeSpan.FromMilliseconds(endMilliseconds)
            : sourceDuration;
        var fadeIn = TimeSpan.FromMilliseconds(fadeInMilliseconds);
        var fadeOut = TimeSpan.FromMilliseconds(fadeOutMilliseconds);

        if (trimEnd > sourceDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trimEndMilliseconds),
                "Trim end cannot exceed the decoded source duration.");
        }

        if (trimEnd <= trimStart)
        {
            throw new ArgumentException(
                "Trim end must be after trim start.");
        }

        var effectiveDuration = trimEnd - trimStart;
        if (effectiveDuration < MinimumPlayableDuration)
        {
            throw new ArgumentException(
                "The playable clip must be at least 100 milliseconds.");
        }

        if (fadeIn > effectiveDuration)
        {
            throw new ArgumentException(
                "Fade in cannot exceed the playable clip duration.");
        }

        if (fadeOut > effectiveDuration)
        {
            throw new ArgumentException(
                "Fade out cannot exceed the playable clip duration.");
        }

        if (fadeIn + fadeOut > effectiveDuration)
        {
            throw new ArgumentException(
                "Fade in plus fade out cannot exceed the playable clip duration.");
        }

        return new AudioClipSettings(
            sourceDuration,
            trimStart,
            trimEnd,
            fadeIn,
            fadeOut);
    }

    private static void ValidateSourceDuration(TimeSpan sourceDuration)
    {
        if (sourceDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDuration),
                "The decoded source duration must be positive.");
        }

        if (sourceDuration.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceDuration),
                "The decoded source duration exceeds the safe editing range.");
        }
    }
}
