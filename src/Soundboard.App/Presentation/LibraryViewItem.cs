using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Soundboard.App.Presentation;

public enum SoundLibraryViewKind
{
    AllSounds,
    Favorites,
    Uncategorized,
    Category
}

/// <summary>
/// One entry in the library sidebar: a built-in view or a user category.
/// </summary>
/// <remarks>
/// Identity is reference-based on purpose. The sidebar keeps the same
/// instances alive while <see cref="SoundCount"/> changes, so selection
/// survives count updates without rebuilding the list.
/// </remarks>
public sealed class LibraryViewItem : INotifyPropertyChanged
{
    private int soundCount;
    private bool isDropTarget;
    private bool startsUserCategorySection;

    public LibraryViewItem(
        SoundLibraryViewKind kind,
        string displayName,
        Guid? categoryId = null)
    {
        Kind = kind;
        DisplayName = displayName;
        CategoryId = categoryId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SoundLibraryViewKind Kind { get; }

    public string DisplayName { get; }

    public Guid? CategoryId { get; }

    public bool IsUserCategory =>
        Kind == SoundLibraryViewKind.Category;

    /// <summary>
    /// Views that represent a real category assignment. Dropping sounds here
    /// has an unambiguous meaning; All Sounds and Favorites do not, so they
    /// reject sound drops instead of guessing.
    /// </summary>
    public bool AcceptsSoundDrops =>
        Kind is SoundLibraryViewKind.Category
            or SoundLibraryViewKind.Uncategorized;

    /// <summary>
    /// Views that name an import destination. Favorites is excluded because
    /// importing cannot make a sound a favorite.
    /// </summary>
    public bool AcceptsFileDrops =>
        Kind is not SoundLibraryViewKind.Favorites;

    /// <summary>
    /// Set while a drag hovers this view so the sidebar can show the target
    /// clearly. Purely transient presentation state.
    /// </summary>
    public bool IsDropTarget
    {
        get => isDropTarget;
        set
        {
            if (isDropTarget == value)
            {
                return;
            }

            isDropTarget = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// True for the first user category, which draws the divider that keeps
    /// built-in views visually separate from user-created categories.
    /// </summary>
    public bool StartsUserCategorySection
    {
        get => startsUserCategorySection;
        set
        {
            if (startsUserCategorySection == value)
            {
                return;
            }

            startsUserCategorySection = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Number of sounds this view currently contains, ignoring any search.
    /// </summary>
    public int SoundCount
    {
        get => soundCount;
        set
        {
            if (soundCount == value)
            {
                return;
            }

            soundCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoundCountText));
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string SoundCountText =>
        soundCount.ToString(CultureInfo.CurrentCulture);

    public string AutomationName =>
        $"{DisplayName}, {soundCount} sound"
        + (soundCount == 1 ? string.Empty : "s");

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
