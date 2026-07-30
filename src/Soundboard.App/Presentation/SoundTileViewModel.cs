using System.ComponentModel;
using System.Runtime.CompilerServices;
using Soundboard.App.Hotkeys;
using Soundboard.App.Storage;
using Soundboard.Audio;

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
    private LoudnessAnalysisKey? loudnessAnalysisKey;
    private LoudnessAnalysisResult? loudnessAnalysisResult;

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

    public bool HasValidNormalizationAnalysis =>
        sound.NormalizeLoudness
        && loudnessAnalysisResult?.IsValid == true
        && loudnessAnalysisKey
            == LoudnessAnalysisKey.Create(
                sound.ContentHash,
                sound.ClipSettings);

    public string NormalizationStateText => !sound.NormalizeLoudness
        ? string.Empty
        : HasValidNormalizationAnalysis
            ? "Normalized"
            : "Normalization needs analysis";

    public LoudnessAnalysisResult? MatchingLoudnessAnalysis =>
        HasValidNormalizationAnalysis ? loudnessAnalysisResult : null;

    public string FormatLabel => sound.FormatLabel;

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

            if (sound.NormalizeLoudness)
            {
                parts.Add(NormalizationStateText);
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
        if (loudnessAnalysisKey
            != LoudnessAnalysisKey.Create(
                replacement.ContentHash,
                replacement.ClipSettings))
        {
            loudnessAnalysisKey = null;
            loudnessAnalysisResult = null;
        }
        if (replacementCategoryName is not null)
        {
            categoryName = replacementCategoryName;
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(OriginalFileName));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(EditedStateText));
        OnPropertyChanged(nameof(HasClipEdits));
        OnPropertyChanged(nameof(HasValidNormalizationAnalysis));
        OnPropertyChanged(nameof(NormalizationStateText));
        OnPropertyChanged(nameof(MatchingLoudnessAnalysis));
        OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(CategoryName));
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(FavoriteStateText));
        OnPropertyChanged(nameof(TileAccent));
        OnPropertyChanged(nameof(HasHotkey));
        OnPropertyChanged(nameof(HotkeyDisplayText));
        OnPropertyChanged(nameof(TileAutomationName));
    }

    public void SetLoudnessAnalysis(
        LoudnessAnalysisKey key,
        LoudnessAnalysisResult? result)
    {
        var currentKey = LoudnessAnalysisKey.Create(
            sound.ContentHash,
            sound.ClipSettings);
        loudnessAnalysisKey = key == currentKey ? key : null;
        loudnessAnalysisResult = key == currentKey ? result : null;
        OnPropertyChanged(nameof(HasValidNormalizationAnalysis));
        OnPropertyChanged(nameof(NormalizationStateText));
        OnPropertyChanged(nameof(MatchingLoudnessAnalysis));
        OnPropertyChanged(nameof(TileAutomationName));
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
