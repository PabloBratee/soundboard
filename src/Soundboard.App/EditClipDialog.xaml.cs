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
    private readonly double masterVolumePercent;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private bool closed;

    public EditClipDialog(
        SoundLibraryEntry sound,
        string managedPath,
        WaveformCacheService waveformCacheService,
        AudioPreviewService previewService,
        AudioEndpoint? previewEndpoint,
        string previewAvailabilityMessage,
        double masterVolumePercent)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        this.sound = sound;
        this.managedPath = managedPath;
        this.waveformCacheService = waveformCacheService;
        this.previewService = previewService;
        this.previewEndpoint = previewEndpoint;
        this.masterVolumePercent = masterVolumePercent;

        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);
        SoundNameTextBlock.Text = sound.DisplayName;
        OriginalFileNameTextBlock.Text = sound.OriginalFileName;
        FormatTextBlock.Text = sound.FormatLabel;
        OriginalDurationTextBlock.Text = FormatTime(sound.Duration);
        PreviewStatusTextBlock.Text = previewAvailabilityMessage;
        PreviewStatusTextBlock.Foreground = previewEndpoint is null
            ? ThemeBrush("ErrorBrush")
            : ThemeBrush("TextMutedBrush");
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

    private async void EditClipDialog_Loaded(object sender, RoutedEventArgs eventArgs)
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
                ? ThemeBrush("TextMutedBrush")
                : ThemeBrush("WarningBrush");
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
                    "Waveform decoding failed. Clip editing remains available: "
                    + exception.Message;
                WaveformStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            }
        }
    }

    private async void PlayPreviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            if (previewEndpoint is null)
            {
                throw new InvalidOperationException(
                    "No safe physical preview device is available.");
            }

            PreviewStatusTextBlock.Text =
                $"Starting local-only preview on {previewEndpoint.FriendlyName}…";
            await previewService.PlayAsync(
                managedPath,
                CreateProposedSettings(),
                previewEndpoint,
                sound.VolumePercent,
                masterVolumePercent,
                lifetimeCancellation.Token);
            PreviewStatusTextBlock.Text =
                $"Playing once through {previewEndpoint.FriendlyName}.";
            PreviewStatusTextBlock.Foreground = ThemeBrush("SuccessBrush");
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing or this preview was superseded.
        }
        catch (Exception exception)
        {
            PreviewStatusTextBlock.Text = $"Preview could not start: {exception.Message}";
            PreviewStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
        }
    }

    private void StopPreviewButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        PreviewStatusTextBlock.Text = "Preview stopped.";
        PreviewStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
    }

    private void ResetButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        WaveformEditor.Reset();
        PreviewStatusTextBlock.Text =
            "Reset to full-duration playback with no fades. Select Save to persist it.";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            _ = CreateProposedSettings();
            ProposedUpdate = new SoundClipMetadataUpdate(
                WaveformEditor.TrimStartMilliseconds,
                WaveformEditor.TrimEndMilliseconds
                    == WaveformEditor.SourceDurationMilliseconds
                        ? null
                        : WaveformEditor.TrimEndMilliseconds,
                WaveformEditor.FadeInMilliseconds,
                WaveformEditor.FadeOutMilliseconds);
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

    private void CancelButton_Click(object sender, RoutedEventArgs eventArgs)
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

    private void PreviewService_PreviewFailed(object? sender, string message)
    {
        if (!closed && !Dispatcher.HasShutdownStarted)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                PreviewStatusTextBlock.Text = message;
                PreviewStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            });
        }
    }

    private void WaveformEditor_ValuesChanged(object? sender, EventArgs eventArgs) =>
        UpdateValueText();

    private void TrimStartEarlierButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustTrimStart(-GetAdjustmentIncrement());
    private void TrimStartLaterButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustTrimStart(GetAdjustmentIncrement());
    private void TrimEndEarlierButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustTrimEnd(-GetAdjustmentIncrement());
    private void TrimEndLaterButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustTrimEnd(GetAdjustmentIncrement());
    private void FadeInDecreaseButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustFadeIn(-GetAdjustmentIncrement());
    private void FadeInIncreaseButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustFadeIn(GetAdjustmentIncrement());
    private void FadeOutDecreaseButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustFadeOut(-GetAdjustmentIncrement());
    private void FadeOutIncreaseButton_Click(object sender, RoutedEventArgs e) =>
        WaveformEditor.AdjustFadeOut(GetAdjustmentIncrement());

    private AudioClipSettings CreateProposedSettings() =>
        AudioClipSettings.Create(
            sound.Duration,
            WaveformEditor.TrimStartMilliseconds,
            WaveformEditor.TrimEndMilliseconds
                == WaveformEditor.SourceDurationMilliseconds
                    ? null
                    : WaveformEditor.TrimEndMilliseconds,
            WaveformEditor.FadeInMilliseconds,
            WaveformEditor.FadeOutMilliseconds);

    private void UpdateValueText()
    {
        TrimStartTextBlock.Text = FormatMilliseconds(WaveformEditor.TrimStartMilliseconds);
        TrimEndTextBlock.Text = FormatMilliseconds(WaveformEditor.TrimEndMilliseconds);
        FadeInTextBlock.Text = FormatMilliseconds(WaveformEditor.FadeInMilliseconds);
        FadeOutTextBlock.Text = FormatMilliseconds(WaveformEditor.FadeOutMilliseconds);
        EffectiveDurationTextBlock.Text =
            FormatMilliseconds(WaveformEditor.EffectiveDurationMilliseconds);
    }

    private static int GetAdjustmentIncrement() =>
        Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            ? 1000
            : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 100;

    private static string FormatMilliseconds(int milliseconds) =>
        FormatTime(TimeSpan.FromMilliseconds(milliseconds));

    private static string FormatTime(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss\.fff")
            : duration.ToString(@"m\:ss\.fff");

    private Brush ThemeBrush(string resourceKey) =>
        TryFindResource(resourceKey) as Brush ?? SystemColors.ControlTextBrush;
}
