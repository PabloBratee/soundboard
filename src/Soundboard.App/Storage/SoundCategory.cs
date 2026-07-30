namespace Soundboard.App.Storage;

public sealed record SoundCategory(
    Guid Id,
    string DisplayName,
    int SortOrder,
    DateTimeOffset CreatedAtUtc);
