using System.ComponentModel;
using System.Runtime.CompilerServices;
using Soundboard.App.Hotkeys;
using Soundboard.App.Storage;

namespace Soundboard.App.Presentation;

public sealed class SoundTileViewModel : INotifyPropertyChanged
{
    private SoundLibraryEntry sound;
    private bool isPlaying;
    private string hotkeyStateText;
    private string? hotkeyError;

    public SoundTileViewModel(SoundLibraryEntry sound)
    {
        this.sound = sound;
        hotkeyStateText = sound.Hotkey is null
            ? "Not assigned"
            : "Assigned · registration pending";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => sound.Id;

    public string DisplayName => sound.DisplayName;

    public string DurationText => sound.Duration.TotalHours >= 1
        ? sound.Duration.ToString(@"h\:mm\:ss")
        : sound.Duration.ToString(@"m\:ss");

    public SoundLibraryEntry Sound => sound;

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

    public void ReplaceSound(SoundLibraryEntry replacement)
    {
        if (replacement.Id != Id)
        {
            throw new ArgumentException(
                "A tile cannot change its stable sound ID.",
                nameof(replacement));
        }

        sound = replacement;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Sound));
        OnPropertyChanged(nameof(HotkeyDisplayText));
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
