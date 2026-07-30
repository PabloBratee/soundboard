namespace Soundboard.App.Presentation;

public enum SoundLibraryViewKind
{
    AllSounds,
    Favorites,
    Uncategorized,
    Category
}

public sealed record LibraryViewItem(
    SoundLibraryViewKind Kind,
    string DisplayName,
    Guid? CategoryId = null)
{
    public bool IsUserCategory =>
        Kind == SoundLibraryViewKind.Category;
}
