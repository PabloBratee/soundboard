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
    private readonly LoudnessAnalysisService loudnessAnalysisService;
    private readonly AudioEndpoint? previewEndpoint;
    private readonly double normalizationTargetLufs;
    private readonly bool limiterEnabled;
    private readonly double limiterCeilingDbfs;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private LoudnessAnalysisOutcome? analysisOutcome;
    private bool analysisInProgress;
    private bool closed;

    public EditClipDialog(
        SoundLibraryEntry sound,
        string managedPath,
        WaveformCacheService waveformCacheService,
        AudioPreviewService previewService,
        LoudnessAnalysisService loudnessAnalysisService,
        AudioEndpoint? previewEndpoint,
        string previewAvailabilityMessage,
        double normalizationTargetLufs,
        bool limiterEnabled,
        double limiterCeilingDbfs)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedPath);
        ArgumentNullException.ThrowIfNull(waveformCacheService);
        ArgumentNullException.ThrowIfNull(previewService);
        ArgumentNullException.ThrowIfNull(loudnessAnalysisService);
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);

        this.sound = sound;
        this.managedPath = managedPath;
        this.waveformCacheService = waveformCacheService;
        this.previewService = previewService;
        this.loudnessAnalysisService = loudnessAnalysisService;
        this.previewEndpoint = previewEndpoint;
        this.normalizationTargetLufs = normalizationTargetLufs;
        this.limiterEnabled = limiterEnabled;
        this.limiterCeilingDbfs = limiterCeilingDbfs;
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
        NormalizeLoudnessCheckBox.IsChecked = sound.NormalizeLoudness;
        NormalizeLoudnessCheckBox.Checked +=
            NormalizeLoudnessCheckBox_Changed;
        NormalizeLoudnessCheckBox.Unchecked +=
            NormalizeLoudnessCheckBox_Changed;
        LoudnessTargetTextBlock.Text =
            $"{normalizationTargetLufs:N1} LUFS";
        previewService.PreviewFailed += PreviewService_PreviewFailed;
        Loaded += EditClipDialog_Loaded;
        Closed += EditClipDialog_Closed;
        UpdateValueText();
    }

    public SoundClipMetadataUpdate? ProposedUpdate { get; private set; }

    public LoudnessAnalysisOutcome? SavedAnalysisOutcome { get; private set; }

    private async void EditClipDialog_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            await LoadCachedAnalysisAsync();
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
                    "Waveform decoding failed. Playback and clip editing "
                    + $"remain available; reopen to retry. {exception.Message}";
                WaveformStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
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
            var normalizationGainDb = 0d;
            if (NormalizeLoudnessCheckBox.IsChecked == true)
            {
                var outcome = await EnsureMatchingAnalysisAsync();
                if (!outcome.Result.IsValid)
                {
                    throw new InvalidOperationException(
                        outcome.Result.InvalidReason
                        ?? "Loudness analysis is not valid.");
                }

                var calculation = LoudnessNormalization.Calculate(
                    outcome.Result,
                    normalizationTargetLufs);
                normalizationGainDb = calculation.AppliedGainDb;
            }

            PreviewStatusTextBlock.Text =
                $"Starting local-only preview on "
                + $"{previewEndpoint.FriendlyName}…";
            PreviewStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
            await previewService.PlayAsync(
                managedPath,
                settings,
                previewEndpoint,
                normalizationGainDb,
                limiterEnabled,
                limiterCeilingDbfs,
                lifetimeCancellation.Token);
            PreviewStatusTextBlock.Text =
                $"Playing once through {previewEndpoint.FriendlyName}. "
                + "Preview is not connected to the virtual microphone mixer.";
            PreviewStatusTextBlock.Foreground = ThemeBrush("SuccessBrush");
        }
        catch (OperationCanceledException)
        {
            // The dialog is closing.
        }
        catch (Exception exception)
        {
            PreviewStatusTextBlock.Text =
                $"Preview could not start: {exception.Message}";
            PreviewStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
        }
    }

    private void StopPreviewButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        previewService.Stop();
        PreviewStatusTextBlock.Text =
            "Preview stopped. The main microphone engine was not changed.";
        PreviewStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
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
        PreviewStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
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
                WaveformEditor.FadeOutMilliseconds,
                NormalizeLoudnessCheckBox.IsChecked == true);
            if (NormalizeLoudnessCheckBox.IsChecked == true)
            {
                var key = LoudnessAnalysisKey.Create(
                    sound.ContentHash,
                    settings);
                if (analysisOutcome?.Key != key
                    || analysisOutcome.Result.IsValid != true)
                {
                    throw new InvalidOperationException(
                        "Analyze the current trim and fade settings before "
                        + "saving with loudness normalization enabled.");
                }

                SavedAnalysisOutcome = analysisOutcome;
            }
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
        NormalizeLoudnessCheckBox.Checked -=
            NormalizeLoudnessCheckBox_Changed;
        NormalizeLoudnessCheckBox.Unchecked -=
            NormalizeLoudnessCheckBox_Changed;
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
                    PreviewStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
                }
            });
    }

    private void WaveformEditor_ValuesChanged(
        object? sender,
        EventArgs eventArgs)
    {
        UpdateValueText();
        UpdateLoudnessPresentation();
    }

    private async void AnalyzeLoudnessButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            await AnalyzeProposedAsync();
        }
        catch (OperationCanceledException)
        {
            // Closing the editor cancels analysis.
        }
        catch (Exception exception)
        {
            LoudnessStatusTextBlock.Text =
                $"Loudness analysis failed: {exception.Message}";
            LoudnessStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
        }
    }

    private async void NormalizeLoudnessCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        UpdateLoudnessPresentation();
        if (NormalizeLoudnessCheckBox.IsChecked == true
            && !HasMatchingValidAnalysis())
        {
            try
            {
                await AnalyzeProposedAsync();
            }
            catch (OperationCanceledException)
            {
                // Closing the editor cancels analysis.
            }
            catch (Exception exception)
            {
                LoudnessStatusTextBlock.Text =
                    $"Loudness analysis failed: {exception.Message}";
                LoudnessStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            }
        }
    }

    private async Task LoadCachedAnalysisAsync()
    {
        var key = LoudnessAnalysisKey.Create(
            sound.ContentHash,
            CreateProposedSettings());
        analysisOutcome = await loudnessAnalysisService.TryLoadCachedAsync(
            key,
            lifetimeCancellation.Token);
        if (!closed)
        {
            UpdateLoudnessPresentation();
        }
    }

    private async Task<LoudnessAnalysisOutcome> EnsureMatchingAnalysisAsync()
    {
        if (HasMatchingAnalysis())
        {
            return analysisOutcome!;
        }

        return await AnalyzeProposedAsync();
    }

    private async Task<LoudnessAnalysisOutcome> AnalyzeProposedAsync()
    {
        if (analysisInProgress)
        {
            throw new InvalidOperationException(
                "Loudness analysis is already running.");
        }

        var settings = CreateProposedSettings();
        var key = LoudnessAnalysisKey.Create(sound.ContentHash, settings);
        analysisInProgress = true;
        AnalyzeLoudnessButton.IsEnabled = false;
        LoudnessStatusTextBlock.Text =
            "Analyzing the effective trimmed and faded clip…";
        LoudnessStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
        try
        {
            var outcome = await loudnessAnalysisService.GetOrAnalyzeAsync(
                key,
                managedPath,
                settings,
                lifetimeCancellation.Token);
            if (closed)
            {
                throw new OperationCanceledException();
            }

            analysisOutcome = outcome;
            UpdateLoudnessPresentation();
            return outcome;
        }
        finally
        {
            analysisInProgress = false;
            if (!closed)
            {
                AnalyzeLoudnessButton.IsEnabled = true;
            }
        }
    }

    private bool HasMatchingAnalysis()
    {
        return analysisOutcome?.Key == LoudnessAnalysisKey.Create(
            sound.ContentHash,
            CreateProposedSettings());
    }

    private bool HasMatchingValidAnalysis()
    {
        return HasMatchingAnalysis()
            && analysisOutcome?.Result.IsValid == true;
    }

    private void UpdateLoudnessPresentation()
    {
        if (analysisInProgress)
        {
            return;
        }

        AnalyzeLoudnessButton.Content = analysisOutcome is null
            ? "Analyze loudness"
            : "Reanalyze";
        if (!HasMatchingAnalysis())
        {
            LoudnessStatusTextBlock.Text = analysisOutcome is null
                ? "Not analyzed. Analysis runs only when requested."
                : "Analysis is stale because trim or fade settings changed.";
            LoudnessStatusTextBlock.Foreground = analysisOutcome is null
                ? ThemeBrush("TextMutedBrush")
                : ThemeBrush("WarningBrush");
            MeasuredLoudnessTextBlock.Text = "—";
            SamplePeakTextBlock.Text = "—";
            RequestedGainTextBlock.Text = "—";
            AppliedGainTextBlock.Text = "—";
            return;
        }

        var result = analysisOutcome!.Result;
        if (!result.IsValid)
        {
            LoudnessStatusTextBlock.Text =
                result.InvalidReason ?? "The clip cannot be normalized.";
            LoudnessStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            MeasuredLoudnessTextBlock.Text = "Unavailable";
            SamplePeakTextBlock.Text =
                $"{result.MaximumSamplePeakDbfs:N1} dBFS";
            RequestedGainTextBlock.Text = "—";
            AppliedGainTextBlock.Text = "—";
            return;
        }

        var calculation = LoudnessNormalization.Calculate(
            result,
            normalizationTargetLufs);
        LoudnessStatusTextBlock.Text = analysisOutcome.Warning
            ?? (analysisOutcome.LoadedFromCache
                ? "Loaded matching analysis from the local cache."
                : "Analysis completed and was cached locally.");
        LoudnessStatusTextBlock.Foreground = analysisOutcome.Warning is null
            ? ThemeBrush("SuccessBrush")
            : ThemeBrush("WarningBrush");
        MeasuredLoudnessTextBlock.Text =
            $"{result.IntegratedLoudnessLufs:N1} LUFS";
        SamplePeakTextBlock.Text =
            $"{result.MaximumSamplePeakDbfs:N1} dBFS";
        RequestedGainTextBlock.Text =
            $"{calculation.RequestedGainDb:+0.0;-0.0;0.0} dB";
        AppliedGainTextBlock.Text =
            $"{calculation.AppliedGainDb:+0.0;-0.0;0.0} dB"
            + (calculation.WasClamped
                ? " (clamped; target cannot be reached)"
                : string.Empty);
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

    private Brush ThemeBrush(string resourceKey)
    {
        return TryFindResource(resourceKey) as Brush
            ?? SystemColors.ControlTextBrush;
    }
}
