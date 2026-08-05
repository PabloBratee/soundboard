using System.ComponentModel;
using System.Runtime.CompilerServices;
using Soundboard.App.Hotkeys;
using Soundboard.App.Storage;

namespace Soundboard.App.Presentation;

public sealed class SoundTileViewModel : INotifyPropertyChanged
{
    private SoundLibraryEntry sound;
    private bool isPlaying;
    private bool canReorder;
    private bool isSelected;
    private bool isSelectionMode;
    private bool showCategoryChip;
    private string categoryName;
    private string reorderAvailabilityText =
        "Drag or use Move earlier/Move later to reorder.";
    private string hotkeyStateText;
    private string? hotkeyError;

    public SoundTileViewModel(
        SoundLibraryEntry sound,
        string categoryName = "Uncategorized")
    {
        this.sound = sound;
        this.categoryName = categoryName;
        hotkeyStateText = sound.Hotkey is null
            ? "Not assigned"
            : "Assigned · registration pending";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => sound.Id;

    public string DisplayName => sound.DisplayName;

    public string OriginalFileName => sound.OriginalFileName;

    public string DurationText => FormatDuration(sound.EffectiveDuration);

    public string EditedStateText => sound.HasClipEdits ? "Trimmed" : string.Empty;

    public bool HasClipEdits => sound.HasClipEdits;

    public string FormatLabel => sound.FormatLabel;

    /// <summary>
    /// Secondary tile line. Duration first because it is what people scan
    /// for; the format follows as supporting detail.
    /// </summary>
    public string MetaText => $"{DurationText} · {FormatLabel}";

    /// <summary>
    /// Everything the compact tile has to trim away, kept available in the
    /// tile tooltip so no information is lost at small sizes.
    /// </summary>
    public string TileDetailText
    {
        get
        {
            var parts = new List<string>
            {
                DurationText,
                FormatLabel,
                CategoryName
            };
            if (HasHotkey)
            {
                parts.Add(HotkeyDisplayText);
            }

            if (HasClipEdits)
            {
                parts.Add("Trimmed");
            }

            parts.Add(OriginalFileName);
            return string.Join(" · ", parts);
        }
    }

    public SoundLibraryEntry Sound => sound;

    public string CategoryName => categoryName;

    public bool IsFavorite => sound.IsFavorite;

    public string FavoriteActionText => IsFavorite
        ? "Remove from favorites"
        : "Add to favorites";

    public string FavoriteStateText => IsFavorite
        ? "Favorite"
        : "Not favorite";

    /// <summary>
    /// Controlled accent preset. The view maps this to a theme brush used
    /// for the tile accent strip and icon surface only, never for the whole
    /// tile background.
    /// </summary>
    public SoundTileAccent TileAccent => sound.TileAccent;

    public bool HasHotkey => sound.Hotkey is not null;

    public string HotkeyDisplayText =>
        sound.Hotkey?.DisplayText ?? "No hotkey";

    public string HotkeyStateText
    {
        get => hotkeyStateText;
        private set
        {
            if (hotkeyStateText == value)
            {
                return;
            }

            hotkeyStateText = value;
            OnPropertyChanged();
        }
    }

    public string? HotkeyError
    {
        get => hotkeyError;
        private set
        {
            if (hotkeyError == value)
            {
                return;
            }

            hotkeyError = value;
            OnPropertyChanged();
        }
    }

    public bool IsPlaying
    {
        get => isPlaying;
        set
        {
            if (isPlaying == value)
            {
                return;
            }

            isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayingStateText));
            OnPropertyChanged(nameof(TileAutomationName));
        }
    }

    public string PlayingStateText => IsPlaying ? "Playing" : "Ready";

    /// <summary>
    /// Transient organization state. It never reaches the library file; the
    /// stored entry in <see cref="Sound"/> stays the only persistent state.
    /// </summary>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value)
            {
                return;
            }

            isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionStateText));
            OnPropertyChanged(nameof(TileAutomationName));
        }
    }

    public bool IsSelectionMode
    {
        get => isSelectionMode;
        set
        {
            if (isSelectionMode == value)
            {
                return;
            }

            isSelectionMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionStateText));
            OnPropertyChanged(nameof(TileAutomationName));
            OnPropertyChanged(nameof(PrimaryActionHelpText));
        }
    }

    /// <summary>
    /// The category chip is only useful where the grid mixes categories, so
    /// it appears in All Sounds, Favorites, and search results.
    /// </summary>
    public bool ShowCategoryChip
    {
        get => showCategoryChip;
        set
        {
            if (showCategoryChip == value)
            {
                return;
            }

            showCategoryChip = value;
            OnPropertyChanged();
        }
    }

    public string SelectionStateText => IsSelectionMode
        ? IsSelected ? "Selected" : "Not selected"
        : string.Empty;

    public string PrimaryActionHelpText => IsSelectionMode
        ? "Activate to select or clear this sound. Hold Ctrl to select one "
            + "more, or Shift to select a range."
        : "Activate to play this sound from the start.";

    public string CategoryChipAutomationName =>
        $"Category {CategoryName}. Activate to move this sound to another "
        + "category.";

    public string MoveToCategoryHelpText =>
        $"Move \"{DisplayName}\" to another category";

    /// <summary>
    /// Full spoken description of the tile. Keeps every state that the
    /// visual design conveys with icons, colour, or badges available to
    /// screen readers as text.
    /// </summary>
    public string TileAutomationName
    {
        get
        {
            var parts = new List<string>
            {
                DisplayName,
                DurationText,
                FormatLabel,
                CategoryName,
                FavoriteStateText,
                PlayingStateText,
                HasHotkey
                    ? $"Hotkey {HotkeyDisplayText}, {HotkeyStateText}"
                    : "No hotkey"
            };

            if (HasClipEdits)
            {
                parts.Add("Trimmed");
            }

            if (IsSelectionMode)
            {
                parts.Add(SelectionStateText);
            }

            return string.Join(", ", parts);
        }
    }

    public bool CanReorder
    {
        get => canReorder;
        set
        {
            if (canReorder == value)
            {
                return;
            }

            canReorder = value;
            OnPropertyChanged();
        }
    }

    public string ReorderAvailabilityText
    {
        get => reorderAvailabilityText;
        set
        {
            if (reorderAvailabilityText == value)
            {
                return;
            }

            reorderAvailabilityText = value;
            OnPropertyChanged();
        }
    }

    public void ReplaceSound(
        SoundLibraryEntry replacement,
        string? replacementCategoryName = null)
    {
        if (replacement.Id != Id)
        {
            throw new ArgumentException(
                "A tile cannot change its stable sound ID.",
                nameof(replacement));
        }

        sound = replacement;
        if (replacementCategoryName is not null)
        {
            categoryName = replacementCategoryName;
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(OriginalFileName));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(EditedStateText));
        OnPropertyChanged(nameof(HasClipEdits));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(TileDetailText));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(FavoriteStateText));
        OnPropertyChanged(nameof(TileAccent));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HotkeyDisplayText));
        OnPropertyChanged(nameof(TileAutomationName));
        OnPropertyChanged(nameof(CategoryChipAutomationName));
        OnPropertyChanged(nameof(MoveToCategoryHelpText));
    }

    public void SetCategoryName(string replacementCategoryName)
    {
        if (categoryName == replacementCategoryName)
        {
            return;
        }

        categoryName = replacementCategoryName;
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(TileAutomationName));
        OnPropertyChanged(nameof(TileDetailText));
        OnPropertyChanged(nameof(CategoryChipAutomationName));
    }

    public void ApplyHotkeyStatus(HotkeyBindingStatus status)
    {
        if (status.Target != HotkeyTarget.ForSound(Id))
        {
            throw new ArgumentException(
                "The hotkey status belongs to another sound.",
                nameof(status));
        }

        HotkeyStateText = status.State switch
        {
            HotkeyRegistrationState.Registered =>
                "Assigned · registered",
            HotkeyRegistrationState.Unavailable =>
                "Assigned · unavailable",
            HotkeyRegistrationState.Disabled =>
                "Assigned · global hotkeys disabled",
            _ => "Not assigned"
        };
        HotkeyError = status.Error;
        OnPropertyChanged(nameof(TileAutomationName));
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss\.f");
    }
}
