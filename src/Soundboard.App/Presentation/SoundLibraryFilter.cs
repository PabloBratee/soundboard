using Soundboard.App.Storage;

namespace Soundboard.App.Presentation;

public static class SoundLibraryFilter
{
    public static bool CanReorder(
        LibraryViewItem? selectedView,
        string? searchText)
    {
        return string.IsNullOrWhiteSpace(searchText)
            && selectedView?.Kind is
                SoundLibraryViewKind.AllSounds
                or SoundLibraryViewKind.Uncategorized
                or SoundLibraryViewKind.Category;
    }

    public static bool MatchesView(
        SoundLibraryEntry sound,
        LibraryViewItem selectedView)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(selectedView);

        return selectedView.Kind switch
        {
            SoundLibraryViewKind.Favorites => sound.IsFavorite,
            SoundLibraryViewKind.Uncategorized =>
                sound.CategoryId is null,
            SoundLibraryViewKind.Category =>
                sound.CategoryId == selectedView.CategoryId,
            _ => true
        };
    }

    public static bool MatchesSearch(
        SoundLibraryEntry sound,
        string categoryName,
        string? searchText)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(categoryName);

        var search = searchText?.Trim();
        return string.IsNullOrEmpty(search)
            || sound.DisplayName.Contains(
                search,
                StringComparison.OrdinalIgnoreCase)
            || sound.OriginalFileName.Contains(
                search,
                StringComparison.OrdinalIgnoreCase)
            || categoryName.Contains(
                search,
                StringComparison.OrdinalIgnoreCase);
    }
}
