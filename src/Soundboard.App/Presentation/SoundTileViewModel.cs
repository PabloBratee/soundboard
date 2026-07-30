using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Soundboard.App.Hotkeys;
using Soundboard.App.Storage;

namespace Soundboard.App.Presentation;

public sealed class SoundTileViewModel : INotifyPropertyChanged
{
    private SoundLibraryEntry sound;
    private bool isPlaying;
    private bool canReorder;
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

    public string DurationText => sound.Duration.TotalHours >= 1
        ? sound.Duration.ToString(@"h\:mm\:ss")
        : sound.Duration.ToString(@"m\:ss");

    public string FormatLabel => sound.FormatLabel;

    public SoundLibraryEntry Sound => sound;

    public string CategoryName => categoryName;

    public bool IsFavorite => sound.IsFavorite;

    public string FavoriteGlyph => IsFavorite ? "★" : "☆";

    public string FavoriteActionText => IsFavorite
        ? "Remove from favorites"
        : "Add to favorites";

    public string FavoriteStateText => IsFavorite
        ? "Favorite"
        : "Not favorite";

    public Brush TileBackground => sound.TileAccent switch
    {
        SoundTileAccent.Blue => Brushes.AliceBlue,
        SoundTileAccent.Purple => new SolidColorBrush(
            Color.FromRgb(246, 239, 255)),
        SoundTileAccent.Green => new SolidColorBrush(
            Color.FromRgb(237, 249, 240)),
        SoundTileAccent.Orange => new SolidColorBrush(
            Color.FromRgb(255, 246, 232)),
        SoundTileAccent.Red => new SolidColorBrush(
            Color.FromRgb(255, 239, 239)),
        SoundTileAccent.Pink => new SolidColorBrush(
            Color.FromRgb(255, 239, 248)),
        SoundTileAccent.Teal => new SolidColorBrush(
            Color.FromRgb(234, 249, 248)),
        _ => Brushes.White
    };

    public Brush TileBorderBrush => sound.TileAccent switch
    {
        SoundTileAccent.Blue => Brushes.SteelBlue,
        SoundTileAccent.Purple => Brushes.MediumPurple,
        SoundTileAccent.Green => Brushes.SeaGreen,
        SoundTileAccent.Orange => Brushes.DarkOrange,
        SoundTileAccent.Red => Brushes.IndianRed,
        SoundTileAccent.Pink => Brushes.DeepPink,
        SoundTileAccent.Teal => Brushes.Teal,
        _ => new SolidColorBrush(Color.FromRgb(203, 210, 218))
    };

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
            OnPropertyChanged(nameof(PlayingLabel));
        }
    }

    public string PlayingLabel => IsPlaying ? "▶ Playing" : "Play";

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
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(FavoriteStateText));
        OnPropertyChanged(nameof(TileBackground));
        OnPropertyChanged(nameof(TileBorderBrush));
        OnPropertyChanged(nameof(HotkeyDisplayText));
    }

    public void SetCategoryName(string replacementCategoryName)
    {
        if (categoryName == replacementCategoryName)
        {
            return;
        }

        categoryName = replacementCategoryName;
        OnPropertyChanged(nameof(CategoryName));
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
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
