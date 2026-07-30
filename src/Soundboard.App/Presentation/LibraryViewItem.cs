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
