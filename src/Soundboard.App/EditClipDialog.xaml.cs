using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Soundboard.App.Storage;
using Soundboard.Audio;

namespace Soundboard.App;

public partial class EditClipDialog : Window
{
    private readonly SoundLibraryEntry sound;
    private readonly string managedPath;
    private readonly WaveformCacheService waveformCacheService;
    private readonly AudioPreviewService previewService;
    private readonly AudioEndpoint? previewEndpoint;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool closed;

    public EditClipDialog(
        SoundLibraryEntry sound,
        string managedPath,
        WaveformCacheService waveformCacheService,
        AudioPreviewService previewService,
        AudioEndpoint? previewEndpoint,
        string previewAvailabilityMessage)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        ArgumentNullException.ThrowIfNull(waveformCacheService);
        ArgumentNullException.ThrowIfNull(previewService);
        InitializeComponent();

        this.sound = sound;
        this.managedPath = managedPath;
        this.waveformCacheService = waveformCacheService;
        this.previewService = previewService;
        this.previewEndpoint = previewEndpoint;
        SoundNameTextBlock.Text = sound.DisplayName;
        OriginalFileNameTextBlock.Text = sound.OriginalFileName;
        FormatTextBlock.Text = sound.FormatLabel;
        OriginalDurationTextBlock.Text = FormatTime(sound.Duration);
        PreviewStatusTextBlock.Text = previewAvailabilityMessage;
        PreviewStatusTextBlock.Foreground = previewEndpoint is null
            ? Brushes.DarkRed
            : Brushes.DimGray;
        WaveformEditor.Configure(
            sound.Duration,
            sound.TrimStartMilliseconds,
            sound.TrimEndMilliseconds,
            sound.FadeInMilliseconds,
            sound.FadeOutMilliseconds);
        WaveformEditor.ValuesChanged += WaveformEditor_ValuesChanged;
        previewService.PreviewFailed += PreviewService_PreviewFailed;
        Loaded += EditClipDialog_Loaded;
        Closed += EditClipDialog_Closed;
        UpdateValueText();
    }

    public SoundClipMetadataUpdate? ProposedUpdate { get; private set; }

    private async void EditClipDialog_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var result = await waveformCacheService.GetOrCreateAsync(
                managedPath,
                sound.ContentHash,
                WaveformCacheService.DefaultBinCount,
                lifetimeCancellation.Token);
            if (closed)
            {
                return;
            }

            WaveformEditor.SetWaveform(result.Data);
            WaveformStatusTextBlock.Text = result.Warning
                ?? (result.LoadedFromCache
                    ? "Loaded waveform from the local derived-data cache."
                    : "Generated waveform from decoded PCM and cached it locally.");
            WaveformStatusTextBlock.Foreground = result.Warning is null
                ? Brushes.DimGray
                : Brushes.DarkOrange;
        }
        catch (OperationCanceledException)
        {
            // Closing the editor safely abandons the display update.
        }
        catch (Exception exception)
        {
            if (!closed)
            {
                WaveformStatusTextBlock.Text =
                    "Waveform decoding failed. Playback and clip editing "
                    + $"remain available; reopen to retry. {exception.Message}";
                WaveformStatusTextBlock.Foreground = Brushes.DarkRed;
            }
        }
    }

    private async void PlayPreviewButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            if (previewEndpoint is null)
            {
                throw new InvalidOperationException(
                    "No safe physical preview device is available.");
            }

            var settings = CreateProposedSettings();
            PreviewStatusTextBlock.Text =
                $"Starting local-only preview on "
                + $"{previewEndpoint.FriendlyName}…";
            PreviewStatusTextBlock.Foreground = Brushes.DimGray;
            await previewService.PlayAsync(
                managedPath,
                settings,
                previewEndpoint,
                lifetimeCancellation.Token);
            PreviewStatusTextBlock.Text =
                $"Playing once through {previewEndpoint.FriendlyName}. "
                + "Preview is not connected to the virtual microphone mixer.";
            PreviewStatusTextBlock.Foreground = Brushes.DarkGreen;
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing.
        }
        catch (Exception exception)
        {
            PreviewStatusTextBlock.Text =
                $"Preview could not start: {exception.Message}";
            PreviewStatusTextBlock.Foreground = Brushes.DarkRed;
        }
    }

    private void StopPreviewButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        PreviewStatusTextBlock.Text =
            "Preview stopped. The main microphone engine was not changed.";
        PreviewStatusTextBlock.Foreground = Brushes.DimGray;
    }

    private void ResetButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        WaveformEditor.Reset();
        PreviewStatusTextBlock.Text =
            "Reset proposed full-duration playback with no fades. "
            + "Select Save to persist it.";
        PreviewStatusTextBlock.Foreground = Brushes.DimGray;
    }

    private void SaveButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            var settings = CreateProposedSettings();
            ProposedUpdate = new SoundClipMetadataUpdate(
                WaveformEditor.TrimStartMilliseconds,
                WaveformEditor.TrimEndMilliseconds
                    == WaveformEditor.SourceDurationMilliseconds
                        ? null
                        : WaveformEditor.TrimEndMilliseconds,
                WaveformEditor.FadeInMilliseconds,
                WaveformEditor.FadeOutMilliseconds);
            _ = settings;
            previewService.Stop();
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"The clip settings are invalid: {exception.Message}",
                "Edit clip",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CancelButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        DialogResult = false;
    }

    private void EditClipDialog_Closed(object? sender, EventArgs eventArgs)
    {
        closed = true;
        lifetimeCancellation.Cancel();
        previewService.Stop();
        previewService.PreviewFailed -= PreviewService_PreviewFailed;
        WaveformEditor.ValuesChanged -= WaveformEditor_ValuesChanged;
        lifetimeCancellation.Dispose();
    }

    private void PreviewService_PreviewFailed(
        object? sender,
        string message)
    {
        if (closed || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (!closed)
                {
                    PreviewStatusTextBlock.Text = message;
                    PreviewStatusTextBlock.Foreground = Brushes.DarkRed;
                }
            });
    }

    private void WaveformEditor_ValuesChanged(
        object? sender,
        EventArgs eventArgs)
    {
        UpdateValueText();
    }

    private void TrimStartEarlierButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustTrimStart(-GetAdjustmentIncrement());

    private void TrimStartLaterButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustTrimStart(GetAdjustmentIncrement());

    private void TrimEndEarlierButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustTrimEnd(-GetAdjustmentIncrement());

    private void TrimEndLaterButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustTrimEnd(GetAdjustmentIncrement());

    private void FadeInDecreaseButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustFadeIn(-GetAdjustmentIncrement());

    private void FadeInIncreaseButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustFadeIn(GetAdjustmentIncrement());

    private void FadeOutDecreaseButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustFadeOut(-GetAdjustmentIncrement());

    private void FadeOutIncreaseButton_Click(
        object sender,
        RoutedEventArgs eventArgs) =>
        WaveformEditor.AdjustFadeOut(GetAdjustmentIncrement());

    private AudioClipSettings CreateProposedSettings()
    {
        return AudioClipSettings.Create(
            sound.Duration,
            WaveformEditor.TrimStartMilliseconds,
            WaveformEditor.TrimEndMilliseconds
                == WaveformEditor.SourceDurationMilliseconds
                    ? null
                    : WaveformEditor.TrimEndMilliseconds,
            WaveformEditor.FadeInMilliseconds,
            WaveformEditor.FadeOutMilliseconds);
    }

    private void UpdateValueText()
    {
        TrimStartTextBlock.Text = FormatMilliseconds(
            WaveformEditor.TrimStartMilliseconds);
        TrimEndTextBlock.Text = FormatMilliseconds(
            WaveformEditor.TrimEndMilliseconds);
        FadeInTextBlock.Text = FormatMilliseconds(
            WaveformEditor.FadeInMilliseconds);
        FadeOutTextBlock.Text = FormatMilliseconds(
            WaveformEditor.FadeOutMilliseconds);
        EffectiveDurationTextBlock.Text = FormatMilliseconds(
            WaveformEditor.EffectiveDurationMilliseconds);
    }

    private static int GetAdjustmentIncrement()
    {
        return Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? 1000
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                ? 10
                : 100;
    }

    private static string FormatMilliseconds(int milliseconds)
    {
        return FormatTime(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static string FormatTime(TimeSpan duration)
    {
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss\.fff")
            : duration.ToString(@"m\:ss\.fff");
    }
}
