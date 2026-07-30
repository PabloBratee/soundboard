namespace Soundboard.Audio;

public sealed record WaveformData(
    int Version,
    int BinCount,
    int SampleRate,
    int ChannelCount,
    TimeSpan SourceDuration,
    IReadOnlyList<float> Peaks);

public sealed record WaveformLoadResult(
    WaveformData Data,
    bool LoadedFromCache,
    string? Warning);
