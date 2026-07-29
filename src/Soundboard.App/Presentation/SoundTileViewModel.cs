using System.ComponentModel;
using System.Runtime.CompilerServices;
using Soundboard.App.Storage;

namespace Soundboard.App.Presentation;

public sealed class SoundTileViewModel : INotifyPropertyChanged
{
    private SoundLibraryEntry sound;
    private bool isPlaying;

    public SoundTileViewModel(SoundLibraryEntry sound)
    {
        this.sound = sound;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id => sound.Id;

    public string DisplayName => sound.DisplayName;

    public string DurationText => sound.Duration.TotalHours >= 1
        ? sound.Duration.ToString(@"h\:mm\:ss")
        : sound.Duration.ToString(@"m\:ss");

    public SoundLibraryEntry Sound => sound;

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
    }

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
    }
}
