using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Soundboard.App.Hotkeys;
using Soundboard.App.Presentation;
using Soundboard.App.Storage;
using Soundboard.Audio;

namespace Soundboard.App;

public partial class MainWindow : Window
{
    private const string SoundDragDataFormat =
        "Soundboard.SoundLibraryEntryId";

    private static readonly TimeSpan SettingsSaveDelay =
        TimeSpan.FromMilliseconds(650);

    private readonly AudioDeviceService audioDeviceService = new();
    private readonly AudioMixEngine audioEngine = new();
    private readonly SoundLibraryStore soundLibraryStore = new();
    private readonly WaveformCacheService waveformCacheService = new();
    private readonly AudioPreviewService previewService = new();
    private readonly LoudnessAnalysisService loudnessAnalysisService = new();
    private readonly ApplicationSettingsStore settingsStore = new();
    private readonly ObservableCollection<SoundTileViewModel> soundTiles = [];
    private readonly ObservableCollection<SoundCategory> soundCategories = [];
    private readonly ObservableCollection<LibraryViewItem> libraryViews = [];
    private readonly SemaphoreSlim libraryActionGate = new(1, 1);
    private readonly SemaphoreSlim soundTriggerGate = new(1, 1);
    private readonly ICollectionView soundTilesView;

    /// <summary>
    /// Advanced controls live in a window that is created once and only
    /// hidden when dismissed. Every control instance and its event wiring
    /// therefore survives for the whole session, so opening or closing
    /// Settings can never disturb the audio engine or device selection.
    /// </summary>
    private readonly SettingsWindow settingsWindow = new();

    private GlobalHotkeyService? hotkeyService;
    private AudioDeviceSnapshot? currentSnapshot;
    private AudioFormatInfo? selectedMicrophoneFormat;
    private AudioFormatInfo? selectedRenderFormat;
    private AudioFormatInfo? selectedMonitorFormat;
    private ApplicationSettings appSettings = ApplicationSettings.Default;
    private CancellationTokenSource? settingsSaveDelayCancellation;
    private Guid? currentSoundId;
    private long currentSoundSessionId;
    private string lastDiagnosticMessage =
        "No engine diagnostic messages.";
    private string lastHotkeyAction = "None";
    private string lastHotkeyRegistrationError = "None";
    private Guid? lastTriggeredSoundId;
    private SoundTriggerSource? lastSoundTriggerSource;
    private bool isRefreshing;
    private bool isImporting;
    private bool isApplyingSettings;
    private bool isClosing;
    private bool shutdownStarted;
    private bool allowClose;
    private Point? soundDragStartPoint;
    private Guid? draggedSoundId;
    private long formatRequestNumber;
    private long soundTriggerRequestGeneration;
    private EmptyStateAction emptyStateAction = EmptyStateAction.Import;

    /// <summary>
    /// Collapses the per-view sound counts in the sidebar when the window
    /// is too narrow to show them without crowding category names.
    /// </summary>
    public static readonly DependencyProperty SidebarCountVisibilityProperty =
        DependencyProperty.Register(
            nameof(SidebarCountVisibility),
            typeof(Visibility),
            typeof(MainWindow),
            new PropertyMetadata(Visibility.Visible));

    public Visibility SidebarCountVisibility
    {
        get => (Visibility)GetValue(SidebarCountVisibilityProperty);
        set => SetValue(SidebarCountVisibilityProperty, value);
    }

    // ---- Advanced controls hosted by the settings window ----------------

    private ComboBox MicrophoneComboBox =>
        settingsWindow.MicrophoneComboBox;

    private ComboBox VirtualOutputComboBox =>
        settingsWindow.VirtualOutputComboBox;

    private ComboBox MonitorOutputComboBox =>
        settingsWindow.MonitorOutputComboBox;

    private Slider MicrophoneVolumeSlider =>
        settingsWindow.MicrophoneVolumeSlider;

    private Slider MonitorVolumeSlider =>
        settingsWindow.MonitorVolumeSlider;

    private Slider NormalizationTargetSlider =>
        settingsWindow.NormalizationTargetSlider;

    private Slider LimiterCeilingSlider =>
        settingsWindow.LimiterCeilingSlider;

    private CheckBox MuteMicrophoneCheckBox =>
        settingsWindow.MuteMicrophoneCheckBox;

    private CheckBox MonitorSoundsCheckBox =>
        settingsWindow.MonitorSoundsCheckBox;

    private CheckBox GlobalHotkeysCheckBox =>
        settingsWindow.GlobalHotkeysCheckBox;

    private CheckBox SafetyLimiterCheckBox =>
        settingsWindow.SafetyLimiterCheckBox;

    private ProgressBar MicrophonePeakProgressBar =>
        settingsWindow.MicrophonePeakProgressBar;

    private ProgressBar MonitorPeakProgressBar =>
        settingsWindow.MonitorPeakProgressBar;

    private ProgressBar OutputPeakProgressBar =>
        settingsWindow.OutputPeakProgressBar;

    private Button RefreshDevicesButton =>
        settingsWindow.RefreshDevicesButton;

    private Button RetryHotkeysButton =>
        settingsWindow.RetryHotkeysButton;

    private Button ClearStopHotkeyButton =>
        settingsWindow.ClearStopHotkeyButton;

    private TextBox DiagnosticStatusTextBox =>
        settingsWindow.DiagnosticStatusTextBox;

    private TextBlock MicrophoneVolumeTextBlock =>
        settingsWindow.MicrophoneVolumeTextBlock;

    private TextBlock MicrophonePeakTextBlock =>
        settingsWindow.MicrophonePeakTextBlock;

    private TextBlock MonitorVolumeTextBlock =>
        settingsWindow.MonitorVolumeTextBlock;

    private TextBlock MonitorPeakTextBlock =>
        settingsWindow.MonitorPeakTextBlock;

    private TextBlock OutputPeakTextBlock =>
        settingsWindow.OutputPeakTextBlock;

    private TextBlock DeviceCountsTextBlock =>
        settingsWindow.DeviceCountsTextBlock;

    private TextBlock VirtualCableStatusTextBlock =>
        settingsWindow.VirtualCableStatusTextBlock;

    private TextBlock MonitorStatusTextBlock =>
        settingsWindow.MonitorStatusTextBlock;

    private TextBlock VirtualRoutingTextBlock =>
        settingsWindow.VirtualRoutingTextBlock;

    private TextBlock MonitorRoutingTextBlock =>
        settingsWindow.MonitorRoutingTextBlock;

    private TextBlock TargetFormatTextBlock =>
        settingsWindow.TargetFormatTextBlock;

    private TextBlock NormalizationTargetTextBlock =>
        settingsWindow.NormalizationTargetTextBlock;

    private TextBlock LimiterCeilingTextBlock =>
        settingsWindow.LimiterCeilingTextBlock;

    private TextBlock LimiterStatusTextBlock =>
        settingsWindow.LimiterStatusTextBlock;

    private TextBlock LimiterGainReductionTextBlock =>
        settingsWindow.LimiterGainReductionTextBlock;

    private TextBlock RegisteredHotkeyCountTextBlock =>
        settingsWindow.RegisteredHotkeyCountTextBlock;

    private TextBlock StopHotkeyDisplayTextBlock =>
        settingsWindow.StopHotkeyDisplayTextBlock;

    private TextBlock StopHotkeyStateTextBlock =>
        settingsWindow.StopHotkeyStateTextBlock;

    private TextBlock HotkeyStatusTextBlock =>
        settingsWindow.HotkeyStatusTextBlock;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.UseDarkTitleBar(this);

        soundTilesView = CollectionViewSource.GetDefaultView(soundTiles);
        soundTilesView.Filter = FilterSoundTile;
        SoundTilesItemsControl.ItemsSource = soundTilesView;
        LibraryViewsListBox.ItemsSource = libraryViews;
        RebuildLibraryViews(
            SoundLibraryViewKind.AllSounds,
            categoryId: null);

        MicrophoneComboBox.SelectionChanged +=
            DeviceComboBox_SelectionChanged;
        VirtualOutputComboBox.SelectionChanged +=
            DeviceComboBox_SelectionChanged;
        MonitorOutputComboBox.SelectionChanged +=
            DeviceComboBox_SelectionChanged;
        MicrophoneVolumeSlider.ValueChanged +=
            MicrophoneVolumeSlider_ValueChanged;
        MuteMicrophoneCheckBox.Checked +=
            MuteMicrophoneCheckBox_Changed;
        MuteMicrophoneCheckBox.Unchecked +=
            MuteMicrophoneCheckBox_Changed;
        SoundVolumeSlider.ValueChanged +=
            SoundVolumeSlider_ValueChanged;
        MonitorSoundsCheckBox.Checked +=
            MonitorSoundsCheckBox_Changed;
        MonitorSoundsCheckBox.Unchecked +=
            MonitorSoundsCheckBox_Changed;
        MonitorVolumeSlider.ValueChanged +=
            MonitorVolumeSlider_ValueChanged;
        GlobalHotkeysCheckBox.Checked +=
            GlobalHotkeysCheckBox_Changed;
        GlobalHotkeysCheckBox.Unchecked +=
            GlobalHotkeysCheckBox_Changed;
        NormalizationTargetSlider.ValueChanged +=
            NormalizationTargetSlider_ValueChanged;
        SafetyLimiterCheckBox.Checked +=
            SafetyLimiterCheckBox_Changed;
        SafetyLimiterCheckBox.Unchecked +=
            SafetyLimiterCheckBox_Changed;
        LimiterCeilingSlider.ValueChanged +=
            LimiterCeilingSlider_ValueChanged;

        // Settings-window commands are wired here rather than in that
        // window's XAML so all application logic stays in one place.
        RefreshDevicesButton.Click += RefreshDevicesButton_Click;
        RetryHotkeysButton.Click += RetryHotkeysButton_Click;
        settingsWindow.AssignStopHotkeyButton.Click +=
            AssignStopHotkeyButton_Click;
        ClearStopHotkeyButton.Click += ClearStopHotkeyButton_Click;
        settingsWindow.StoragePathsTextBox.Text = string.Join(
            Environment.NewLine,
            $"Library:        {soundLibraryStore.RootPath}",
            $"Waveform cache: {waveformCacheService.WaveformsPath}",
            $"Analysis cache: {loudnessAnalysisService.AnalysisPath}");

        SizeChanged += MainWindow_SizeChanged;

        audioEngine.StateChanged += AudioEngine_StateChanged;
        audioEngine.ErrorOccurred += AudioEngine_ErrorOccurred;
        audioEngine.PeakLevelsChanged += AudioEngine_PeakLevelsChanged;
        audioEngine.SoundPlaybackStateChanged +=
            AudioEngine_SoundPlaybackStateChanged;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        UpdateLibraryPresentation();
        UpdateEnginePresentation();
        UpdateControlAvailability();
    }

    private async void MainWindow_Loaded(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            await LoadLibraryAndSettingsAsync();
            InitializeGlobalHotkeys();
            await RefreshDevicesAsync(
                appSettings.MicrophoneEndpointId,
                appSettings.VirtualOutputEndpointId,
                appSettings.MonitorOutputEndpointId);
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Application startup could not be completed: "
                + exception.Message);
        }
    }

    private async Task LoadLibraryAndSettingsAsync()
    {
        var libraryResult = await soundLibraryStore.LoadAsync();
        soundCategories.Clear();
        foreach (var category in libraryResult.Categories
                     .OrderBy(category => category.SortOrder))
        {
            soundCategories.Add(category);
        }

        soundTiles.Clear();
        foreach (var sound in libraryResult.Sounds)
        {
            soundTiles.Add(
                new SoundTileViewModel(
                    sound,
                    GetCategoryName(sound.CategoryId)));
        }

        RebuildLibraryViews(
            SoundLibraryViewKind.AllSounds,
            categoryId: null);
        UpdateLibraryPresentation();

        var settingsResult = await settingsStore.LoadAsync();
        appSettings = settingsResult.Settings;
        ApplySettingsToWindowAndControls();
        await RefreshCachedAnalysisStatesAsync();

        var warnings = new List<string>();
        if (settingsResult.Warning is not null)
        {
            warnings.Add(settingsResult.Warning);
        }

        warnings.AddRange(libraryResult.Warnings);
        if (warnings.Count > 0)
        {
            ErrorTextBlock.Text = string.Join(
                Environment.NewLine,
                warnings);
            lastDiagnosticMessage = warnings[^1];
        }

        StatusTextBlock.Text =
            $"Loaded {soundTiles.Count} sound(s) from the local library.";
        RefreshDiagnosticStatus();
    }

    private async Task RefreshCachedAnalysisStatesAsync()
    {
        foreach (var tile in soundTiles.Where(
                     candidate => candidate.Sound.NormalizeLoudness))
        {
            var key = LoudnessAnalysisKey.Create(
                tile.Sound.ContentHash,
                tile.Sound.ClipSettings);
            var cached = await loudnessAnalysisService.TryLoadCachedAsync(key);
            if (cached is not null)
            {
                tile.SetLoudnessAnalysis(key, cached.Result);
            }
        }
    }

    private void InitializeGlobalHotkeys()
    {
        hotkeyService = new GlobalHotkeyService(
            this,
            appSettings.GlobalHotkeysEnabled);
        hotkeyService.HotkeyInvoked +=
            HotkeyService_HotkeyInvoked;

        foreach (var tile in soundTiles)
        {
            if (tile.Sound.Hotkey is not null)
            {
                hotkeyService.LoadPersistedBinding(
                    HotkeyTarget.ForSound(tile.Id),
                    tile.Sound.Hotkey);
            }
        }

        if (appSettings.StopSoundHotkey is not null)
        {
            hotkeyService.LoadPersistedBinding(
                HotkeyTarget.StopSound,
                appSettings.StopSoundHotkey);
        }

        UpdateHotkeyPresentation();
        var unavailable = hotkeyService.Statuses
            .Where(
                status =>
                    status.State
                    == HotkeyRegistrationState.Unavailable)
            .ToArray();
        if (unavailable.Length > 0)
        {
            lastHotkeyRegistrationError =
                string.Join(
                    " | ",
                    unavailable.Select(
                        status =>
                            status.Error
                            ?? $"{status.Hotkey?.DisplayText} is unavailable."));
            HotkeyStatusTextBlock.Text =
                $"{unavailable.Length} assigned hotkey(s) are unavailable. "
                + "Other valid hotkeys remain active.";
        }
    }

    private void ApplySettingsToWindowAndControls()
    {
        isApplyingSettings = true;
        try
        {
            MicrophoneVolumeSlider.Value =
                appSettings.MicrophoneVolume * 100d;
            MuteMicrophoneCheckBox.IsChecked =
                appSettings.MicrophoneMuted;
            SoundVolumeSlider.Value =
                appSettings.SoundVolume * 100d;
            MonitorSoundsCheckBox.IsChecked =
                appSettings.MonitoringEnabled;
            MonitorVolumeSlider.Value =
                appSettings.MonitorVolume * 100d;
            GlobalHotkeysCheckBox.IsChecked =
                appSettings.GlobalHotkeysEnabled;
            NormalizationTargetSlider.Value =
                appSettings.NormalizationTargetLufs;
            SafetyLimiterCheckBox.IsChecked =
                appSettings.SafetyLimiterEnabled;
            LimiterCeilingSlider.Value =
                appSettings.SafetyLimiterCeilingDbfs;
            audioEngine.SafetyLimiterEnabled =
                appSettings.SafetyLimiterEnabled;
            audioEngine.SafetyLimiterCeilingDbfs =
                appSettings.SafetyLimiterCeilingDbfs;
            UpdateLoudnessAndLimiterPresentation();

            if (appSettings.WindowWidth is { } width)
            {
                Width = width;
            }

            if (appSettings.WindowHeight is { } height)
            {
                Height = height;
            }

            if (appSettings.WindowLeft is { } left
                && appSettings.WindowTop is { } top
                && IsVisibleWindowPosition(left, top, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }

            if (appSettings.WindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        finally
        {
            isApplyingSettings = false;
        }
    }

    private void SettingsButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        // Purely a window operation: no engine, device, or hotkey state
        // is touched when the settings surface is shown.
        settingsWindow.ShowOrActivate(this);
    }

    private void ManageCategoriesButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        UpdateCategoryControlAvailability();
        OpenAttachedContextMenu(sender);
    }

    private void SoundOverflowButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        OpenAttachedContextMenu(sender);
    }

    private static void OpenAttachedContextMenu(object sender)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } element)
        {
            return;
        }

        menu.PlacementTarget = element;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void MainWindow_SizeChanged(
        object sender,
        SizeChangedEventArgs eventArgs)
    {
        if (!eventArgs.WidthChanged)
        {
            return;
        }

        // Narrow windows keep category names readable by giving up the
        // sidebar counts and some sidebar width instead.
        var narrow = eventArgs.NewSize.Width < 1000d;
        SidebarColumn.Width = new GridLength(narrow ? 158d : 216d);
        SidebarCountVisibility = narrow
            ? Visibility.Collapsed
            : Visibility.Visible;
        SidebarHintTextBlock.Visibility = narrow
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private Brush ThemeBrush(string resourceKey)
    {
        return TryFindResource(resourceKey) as Brush
            ?? SystemColors.ControlTextBrush;
    }

    private string ThemeGlyph(string resourceKey)
    {
        return TryFindResource(resourceKey) as string ?? string.Empty;
    }

    private async void RefreshDevicesButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            await RefreshDevicesAsync();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Audio-device discovery failed: {exception.Message}");
        }
    }

    private async Task RefreshDevicesAsync(
        string? preferredCaptureId = null,
        string? preferredRenderId = null,
        string? preferredMonitorId = null)
    {
        if (audioEngine.State != AudioEngineState.Stopped)
        {
            ShowUiError(
                "Stop the audio engine before refreshing devices.");
            return;
        }

        var selectedCaptureId = preferredCaptureId
            ?? (MicrophoneComboBox.SelectedItem as AudioEndpoint)?.DeviceId;
        var selectedRenderId = preferredRenderId
            ?? (VirtualOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId;
        var selectedMonitorId = preferredMonitorId
            ?? (MonitorOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId;

        isRefreshing = true;
        isApplyingSettings = true;
        UpdateControlAvailability();
        DeviceCountsTextBlock.Text = "Discovering active endpoints…";
        StatusTextBlock.Text = "Querying Windows Core Audio endpoints…";
        ErrorTextBlock.Text = string.Empty;

        try
        {
            var snapshot = await Task.Run(
                audioDeviceService.GetActiveDevices);
            currentSnapshot = snapshot;

            MicrophoneComboBox.ItemsSource = snapshot.CaptureEndpoints;
            VirtualOutputComboBox.ItemsSource = snapshot.RenderEndpoints;
            var physicalRenderEndpoints = snapshot.RenderEndpoints
                .Where(endpoint => !endpoint.IsLikelyVbCable)
                .ToArray();
            MonitorOutputComboBox.ItemsSource = physicalRenderEndpoints;

            MicrophoneComboBox.SelectedItem =
                FindById(snapshot.CaptureEndpoints, selectedCaptureId)
                ?? snapshot.CaptureEndpoints.FirstOrDefault(
                    endpoint => endpoint.IsDefault)
                ?? snapshot.CaptureEndpoints.FirstOrDefault();

            var preferredVirtualCable = snapshot.RenderEndpoints
                .FirstOrDefault(
                    endpoint =>
                        endpoint.IsLikelyVbCable
                        && endpoint.FriendlyName.Contains(
                            "CABLE Input",
                            StringComparison.OrdinalIgnoreCase))
                ?? snapshot.RenderEndpoints.FirstOrDefault(
                    endpoint => endpoint.IsLikelyVbCable);

            VirtualOutputComboBox.SelectedItem =
                FindById(snapshot.RenderEndpoints, selectedRenderId)
                ?? preferredVirtualCable
                ?? snapshot.RenderEndpoints.FirstOrDefault();

            var savedMonitor = FindById(
                snapshot.RenderEndpoints,
                selectedMonitorId);
            var savedMonitorRejected = savedMonitor?.IsLikelyVbCable == true;
            MonitorOutputComboBox.SelectedItem =
                (savedMonitor is { IsLikelyVbCable: false }
                    ? FindById(physicalRenderEndpoints, savedMonitor.DeviceId)
                    : null)
                ?? physicalRenderEndpoints.FirstOrDefault(
                    endpoint => endpoint.IsDefault)
                ?? physicalRenderEndpoints.FirstOrDefault();

            DeviceCountsTextBlock.Text =
                $"{snapshot.CaptureEndpoints.Count} capture, "
                + $"{snapshot.RenderEndpoints.Count} render";

            UpdateVirtualCableStatus(snapshot);
            StatusTextBlock.Text =
                "Device discovery completed. No Windows defaults were "
                + "changed.";
            lastDiagnosticMessage =
                snapshot.Warnings.Count == 0
                    ? "Device discovery completed without warnings."
                    : string.Join(" | ", snapshot.Warnings);
            await UpdateSelectedFormatsAsync();
            UpdateRoutingStatusForSelection();
            if (savedMonitorRejected)
            {
                var warning =
                    $"The saved monitor endpoint \"{savedMonitor!.FriendlyName}\" "
                    + "appears to be a virtual cable and was rejected. A "
                    + "physical render endpoint was selected instead.";
                ErrorTextBlock.Text = warning;
                StatusTextBlock.Text =
                    "A saved virtual monitor endpoint was rejected. Review "
                    + "the selected physical monitor output before starting.";
                lastDiagnosticMessage = warning;
            }
        }
        finally
        {
            isApplyingSettings = false;
            isRefreshing = false;
            UpdateSettingsFromUi();
            ScheduleSettingsSave();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
    }

    private async void DeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        try
        {
            await UpdateSelectedFormatsAsync();
            UpdateRoutingStatusForSelection();
            UpdateSettingsFromUi();
            ScheduleSettingsSave();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The selected endpoint format could not be read: "
                + exception.Message);
        }
    }

    private async Task UpdateSelectedFormatsAsync()
    {
        var requestNumber = Interlocked.Increment(
            ref formatRequestNumber);
        var microphone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        var monitor =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;

        AudioFormatInfo? microphoneFormat = null;
        AudioFormatInfo? renderFormat = null;
        AudioFormatInfo? monitorFormat = null;

        if (microphone is not null)
        {
            microphoneFormat = await Task.Run(
                () => audioDeviceService.GetEndpointMixFormat(
                    microphone.DeviceId,
                    AudioDeviceDirection.Capture));
        }

        if (render is not null)
        {
            renderFormat = await Task.Run(
                () => audioDeviceService.GetEndpointMixFormat(
                    render.DeviceId,
                    AudioDeviceDirection.Render));
        }

        if (monitor is not null)
        {
            monitorFormat = await Task.Run(
                () => audioDeviceService.GetEndpointMixFormat(
                    monitor.DeviceId,
                    AudioDeviceDirection.Render));
        }

        if (requestNumber != Interlocked.Read(ref formatRequestNumber)
            || isClosing)
        {
            return;
        }

        selectedMicrophoneFormat = microphoneFormat;
        selectedRenderFormat = renderFormat;
        selectedMonitorFormat = monitorFormat;
        TargetFormatTextBlock.Text = renderFormat is null
            ? "Select a render endpoint."
            : $"{renderFormat.SampleRate:N0} Hz, "
                + $"{FormatChannelCount(renderFormat.Channels)}, "
                + "32-bit IEEE floating point mixer target "
                + $"(endpoint mix format: {renderFormat.SampleFormat})";
        UpdateRoutingExplanation();
    }

    private async void StartEngineButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var microphone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        var monitor =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        var monitoringEnabled =
            MonitorSoundsCheckBox.IsChecked == true;

        if (microphone is null || render is null)
        {
            ShowUiError(
                "Select a microphone and a VB-CABLE render endpoint first.");
            return;
        }

        ErrorTextBlock.Text = string.Empty;
        StatusTextBlock.Text = "Starting the audio engine…";
        UpdateControlAvailability();

        try
        {
            await Task.Run(
                () => audioEngine.Start(
                    microphone.DeviceId,
                    render.DeviceId,
                    new AudioMonitorConfiguration(
                        monitoringEnabled,
                        monitor?.DeviceId)));

            var monitorDiagnostics = audioEngine.Diagnostics;
            var monitorReady =
                monitorDiagnostics?.MonitorInitializationStatus == "Ready";
            StatusTextBlock.Text = monitorReady
                ? "Audio engine is running. Microphone plus soundboard is "
                    + $"going to {render.FriendlyName}; soundboard-only "
                    + $"monitoring is going to "
                    + $"{monitorDiagnostics!.MonitorFriendlyName}."
                : "Audio engine is running and writing microphone plus "
                    + $"soundboard to {render.FriendlyName}. "
                    + (monitoringEnabled
                        ? "Sound monitoring is unavailable; see the warning."
                        : "Sound monitoring is disabled.");
            lastDiagnosticMessage = monitorReady
                ? "The virtual and sound-only monitor outputs started "
                    + "successfully."
                : monitorDiagnostics?.LastMonitorWarningOrError
                    ?? "The virtual audio engine started successfully.";
            RefreshDiagnosticStatus();
        }
        catch (Exception exception)
        {
            ShowUiError(exception.Message);
        }
        finally
        {
            UpdateControlAvailability();
        }
    }

    private async void StopEngineButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        StatusTextBlock.Text = "Stopping the audio engine…";
        ErrorTextBlock.Text = string.Empty;
        UpdateControlAvailability();

        try
        {
            await Task.Run(audioEngine.Stop);
            StatusTextBlock.Text =
                "Audio engine stopped. Audio devices were released.";
            lastDiagnosticMessage =
                "The audio engine stopped cleanly.";
            RefreshDiagnosticStatus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The audio engine could not stop cleanly: "
                + exception.Message);
        }
        finally
        {
            UpdateControlAvailability();
        }
    }

    private void MicrophoneVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        MicrophoneVolumeTextBlock.Text = $"{percent}%";
        audioEngine.MicrophoneVolume = percent / 100f;
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void MuteMicrophoneCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        audioEngine.MicrophoneMuted =
            MuteMicrophoneCheckBox.IsChecked == true;
        UpdateEnginePresentation();
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void SoundVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        SoundVolumeTextBlock.Text = $"{percent}%";
        audioEngine.SoundVolume = percent / 100f;
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void MonitorSoundsCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        UpdateMonitorStatusForSelection();
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
        UpdateControlAvailability();
        RefreshDiagnosticStatus();
    }

    private void MonitorVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        MonitorVolumeTextBlock.Text = $"{percent}%";
        audioEngine.MonitorVolume = percent / 100f;
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void NormalizationTargetSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        UpdateLoudnessAndLimiterPresentation();
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void SafetyLimiterCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        audioEngine.SafetyLimiterEnabled =
            SafetyLimiterCheckBox.IsChecked == true;
        UpdateLoudnessAndLimiterPresentation();
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
        RefreshDiagnosticStatus();
    }

    private void LimiterCeilingSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var ceiling = Math.Round(eventArgs.NewValue, 1);
        audioEngine.SafetyLimiterCeilingDbfs = ceiling;
        UpdateLoudnessAndLimiterPresentation();
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
        RefreshDiagnosticStatus();
    }

    private void UpdateLoudnessAndLimiterPresentation()
    {
        if (NormalizationTargetTextBlock is null)
        {
            return;
        }

        NormalizationTargetTextBlock.Text =
            $"{NormalizationTargetSlider.Value:N1} LUFS";
        LimiterCeilingTextBlock.Text =
            $"{LimiterCeilingSlider.Value:N1} dBFS";
        var enabled = SafetyLimiterCheckBox.IsChecked == true;
        var diagnostics = audioEngine.Diagnostics;
        LimiterStatusTextBlock.Text = enabled
            ? $"Enabled · "
                + $"{(diagnostics?.SafetyLimiterLookahead
                    ?? SamplePeakLimiter.DefaultLookahead)
                    .TotalMilliseconds:N0} ms lookahead"
            : "Disabled · output is bypassing safety limiting";
        LimiterStatusTextBlock.Foreground = enabled
            ? ThemeBrush("SuccessBrush")
            : ThemeBrush("WarningBrush");
        LimiterGainReductionTextBlock.Text =
            "Current gain reduction — Virtual: "
            + $"{diagnostics?.VirtualLimiterCurrentGainReductionDb ?? 0f:N1} dB"
            + " · Monitor: "
            + $"{diagnostics?.MonitorLimiterCurrentGainReductionDb ?? 0f:N1} dB";
        settingsWindow.LimiterMaximumReductionTextBlock.Text =
            "Maximum gain reduction — Virtual: "
            + $"{diagnostics?.VirtualLimiterMaximumGainReductionDb ?? 0f:N1} dB"
            + " · Monitor: "
            + $"{diagnostics?.MonitorLimiterMaximumGainReductionDb ?? 0f:N1} dB";
        UpdateStatusChips();
    }

    private void ImportSoundsButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import sounds",
            Filter = "Audio files (*.wav;*.mp3;*.ogg;*.opus)"
                + "|*.wav;*.mp3;*.ogg;*.opus"
                + "|WAV files (*.wav)|*.wav"
                + "|MP3 files (*.mp3)|*.mp3"
                + "|Ogg audio (*.ogg;*.opus)|*.ogg;*.opus",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = ImportPathsFromUiAsync(
                dialog.FileNames,
                categoryId: null);
        }
    }

    private void SoundboardDropArea_PreviewDragOver(
        object sender,
        DragEventArgs eventArgs)
    {
        if (eventArgs.Data.GetDataPresent(SoundDragDataFormat))
        {
            eventArgs.Effects = CanReorderSounds
                ? DragDropEffects.Move
                : DragDropEffects.None;
            eventArgs.Handled = false;
            return;
        }

        eventArgs.Effects = eventArgs.Data.GetDataPresent(
            DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private void SoundboardDropArea_Drop(
        object sender,
        DragEventArgs eventArgs)
    {
        if (eventArgs.Data.GetData(DataFormats.FileDrop)
            is string[] paths)
        {
            _ = ImportPathsFromUiAsync(paths, categoryId: null);
        }
    }

    private async Task ImportPathsFromUiAsync(
        IReadOnlyCollection<string> sourcePaths,
        Guid? categoryId)
    {
        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        isImporting = true;
        UpdateControlAvailability();
        ErrorTextBlock.Text = string.Empty;
        StatusTextBlock.Text =
            $"Inspecting and importing {sourcePaths.Count} file(s)…";

        try
        {
            var result = await soundLibraryStore.ImportAsync(
                sourcePaths,
                categoryId);
            foreach (var sound in result.Imported)
            {
                soundTiles.Add(
                    new SoundTileViewModel(
                        sound,
                        GetCategoryName(sound.CategoryId)));
            }

            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            StatusTextBlock.Text = result.ToSummary();
            ErrorTextBlock.Text = BuildImportDetails(result);
            lastDiagnosticMessage = result.ToSummary();
            RefreshDiagnosticStatus();
        }
        catch (Exception exception)
        {
            ShowUiError($"Import failed: {exception.Message}");
        }
        finally
        {
            isImporting = false;
            libraryActionGate.Release();
            UpdateControlAvailability();
        }
    }

    private async void PlayTileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        await TriggerSoundAsync(tile.Id, SoundTriggerSource.Mouse);
    }

    private async void StopSoundButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await StopCurrentSoundAsync(SoundTriggerSource.Mouse);
    }

    private async Task TriggerSoundAsync(
        Guid soundId,
        SoundTriggerSource source)
    {
        var requestGeneration = Interlocked.Increment(
            ref soundTriggerRequestGeneration);
        await soundTriggerGate.WaitAsync();
        try
        {
            if (isClosing)
            {
                return;
            }

            lastTriggeredSoundId = soundId;
            lastSoundTriggerSource = source;
            lastHotkeyAction = source == SoundTriggerSource.Hotkey
                ? $"Requested sound {soundId}"
                : lastHotkeyAction;

            var engineState = audioEngine.State;
            if (engineState != AudioEngineState.Running)
            {
                var message = engineState switch
                {
                    AudioEngineState.Stopped =>
                        "Hotkey ignored because the audio engine is stopped. "
                        + "Start it manually in Soundboard.",
                    AudioEngineState.Starting =>
                        "Sound request ignored while the audio engine is starting.",
                    AudioEngineState.Stopping =>
                        "Sound request ignored while the audio engine is stopping.",
                    AudioEngineState.Faulted =>
                        "Sound request ignored because the audio engine is faulted.",
                    _ =>
                        $"Sound request ignored while the audio engine is "
                        + $"{engineState}."
                };
                StatusTextBlock.Text = source == SoundTriggerSource.Mouse
                    ? "Start the audio engine before playing a sound. "
                        + "Sound tiles never start audio routing automatically."
                    : message;
                lastDiagnosticMessage = message;
                RefreshDiagnosticStatus();
                return;
            }

            await libraryActionGate.WaitAsync();
            try
            {
                var tile = FindTile(soundId);
                if (tile is null)
                {
                    var message =
                        $"Sound request ignored because sound {soundId} "
                        + "is no longer in the library.";
                    StatusTextBlock.Text = message;
                    lastDiagnosticMessage = message;
                    return;
                }

                var managedPath =
                    soundLibraryStore.GetManagedFilePath(tile.Sound);
                if (!File.Exists(managedPath))
                {
                    var message =
                        $"The managed file for \"{tile.DisplayName}\" "
                        + "is missing. The sound was not played.";
                    ErrorTextBlock.Text = message;
                    StatusTextBlock.Text =
                        "The requested sound could not be played.";
                    lastDiagnosticMessage = message;
                    return;
                }

                var normalizationGainDb = 0d;
                ErrorTextBlock.Text = string.Empty;
                if (tile.Sound.NormalizeLoudness)
                {
                    var key = LoudnessAnalysisKey.Create(
                        tile.Sound.ContentHash,
                        tile.Sound.ClipSettings);
                    var result = tile.MatchingLoudnessAnalysis;
                    if (result is null)
                    {
                        StatusTextBlock.Text =
                            $"Analyzing loudness for \"{tile.DisplayName}\"…";
                        var outcome =
                            await loudnessAnalysisService.GetOrAnalyzeAsync(
                                key,
                                managedPath,
                                tile.Sound.ClipSettings);
                        tile.SetLoudnessAnalysis(key, outcome.Result);
                        result = outcome.Result;
                        if (outcome.Warning is not null)
                        {
                            ErrorTextBlock.Text = outcome.Warning;
                        }
                    }

                    if (!result.IsValid)
                    {
                        var message =
                            $"\"{tile.DisplayName}\" could not be played with "
                            + "normalization enabled: "
                            + (result.InvalidReason
                                ?? "loudness analysis is invalid.");
                        ErrorTextBlock.Text = message;
                        StatusTextBlock.Text =
                            "The requested sound was not played. The "
                            + "microphone engine remains active.";
                        lastDiagnosticMessage = message;
                        return;
                    }

                    var calculation = LoudnessNormalization.Calculate(
                        result,
                        appSettings.NormalizationTargetLufs);
                    normalizationGainDb = calculation.AppliedGainDb;
                    if (calculation.WasClamped)
                    {
                        ErrorTextBlock.Text =
                            $"Normalization for \"{tile.DisplayName}\" was "
                            + $"clamped from "
                            + $"{calculation.RequestedGainDb:+0.0;-0.0;0.0} dB "
                            + $"to "
                            + $"{calculation.AppliedGainDb:+0.0;-0.0;0.0} dB.";
                    }
                }

                if (requestGeneration
                        != Interlocked.Read(ref soundTriggerRequestGeneration)
                    || isClosing)
                {
                    return;
                }

                await Task.Run(
                    () => audioEngine.PlaySound(
                        tile.Id,
                        managedPath,
                        tile.Sound.ClipSettings,
                        normalizationGainDb));

                if (audioEngine.CurrentSoundId == tile.Id)
                {
                    StatusTextBlock.Text =
                        $"Playing {tile.DisplayName} once from its edited start "
                        + $"({source.ToString().ToLowerInvariant()}).";
                }
            }
            finally
            {
                libraryActionGate.Release();
            }
        }
        catch (Exception exception)
        {
            var message =
                $"Sound playback could not be started: {exception.Message}";
            ErrorTextBlock.Text = message;
            StatusTextBlock.Text =
                "The requested sound could not be played.";
            lastDiagnosticMessage = message;
        }
        finally
        {
            soundTriggerGate.Release();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
    }

    private async Task StopCurrentSoundAsync(SoundTriggerSource source)
    {
        Interlocked.Increment(ref soundTriggerRequestGeneration);
        await soundTriggerGate.WaitAsync();
        try
        {
            if (isClosing)
            {
                return;
            }

            lastSoundTriggerSource = source;
            if (source == SoundTriggerSource.Hotkey)
            {
                lastHotkeyAction = "Stop current sound";
            }

            await Task.Run(audioEngine.StopSound);
            StatusTextBlock.Text =
                "Sound playback stopped. The microphone and audio engine "
                + "remain active.";
        }
        catch (Exception exception)
        {
            var message =
                $"Sound playback could not be stopped: {exception.Message}";
            ErrorTextBlock.Text = message;
            StatusTextBlock.Text =
                "The requested sound action did not complete.";
            lastDiagnosticMessage = message;
        }
        finally
        {
            soundTriggerGate.Release();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
    }

    private async void HotkeyService_HotkeyInvoked(
        object? sender,
        HotkeyInvokedEventArgs eventArgs)
    {
        try
        {
            if (isClosing
                || hotkeyService is null
                || !hotkeyService.Enabled
                || GlobalHotkeysCheckBox.IsChecked != true)
            {
                return;
            }

            if (eventArgs.Target.Kind == HotkeyTargetKind.StopSound)
            {
                await StopCurrentSoundAsync(SoundTriggerSource.Hotkey);
            }
            else
            {
                await TriggerSoundAsync(
                    eventArgs.Target.SoundId,
                    SoundTriggerSource.Hotkey);
            }
        }
        catch (Exception exception)
        {
            var message =
                $"A global hotkey action failed safely: {exception.Message}";
            ErrorTextBlock.Text = message;
            StatusTextBlock.Text =
                "The global hotkey action did not complete.";
            lastDiagnosticMessage = message;
            RefreshDiagnosticStatus();
        }
    }

    private void GlobalHotkeysCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (isApplyingSettings || hotkeyService is null)
        {
            return;
        }

        try
        {
            var enabled = GlobalHotkeysCheckBox.IsChecked == true;
            hotkeyService.SetEnabled(enabled);
            appSettings = appSettings with
            {
                GlobalHotkeysEnabled = enabled
            };
            ScheduleSettingsSave();
            StatusTextBlock.Text = enabled
                ? "Global hotkeys enabled. Valid assignments were retried."
                : "Global hotkeys disabled. Assignments were preserved.";
            lastHotkeyAction = enabled
                ? "Enabled global hotkeys"
                : "Disabled global hotkeys";
            UpdateHotkeyPresentation();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Global hotkeys could not be updated: {exception.Message}");
        }
    }

    private void RetryHotkeysButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (hotkeyService is null)
        {
            return;
        }

        try
        {
            var statuses = hotkeyService.RetryUnavailable();
            var unavailable = statuses.Count(
                status =>
                    status.State
                    == HotkeyRegistrationState.Unavailable);
            StatusTextBlock.Text = unavailable == 0
                ? "All assigned hotkeys are now registered."
                : $"Retried unavailable hotkeys; {unavailable} remain "
                    + "unavailable.";
            lastHotkeyAction = "Retried unavailable hotkeys";
            UpdateHotkeyPresentation();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Hotkey registrations could not be retried: "
                + exception.Message);
        }
    }

    private async void AssignTileHotkeyButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        var dialog = new HotkeyAssignmentDialog(
            $"Sound: {tile.DisplayName}",
            tile.Sound.Hotkey)
        {
            Owner = this
        };
        if (!ShowHotkeyAssignmentDialog(dialog))
        {
            return;
        }

        var proposed = dialog.ClearRequested
            ? null
            : dialog.ProposedHotkey;
        await ApplySoundHotkeyAsync(tile, proposed);
    }

    private async Task ApplySoundHotkeyAsync(
        SoundTileViewModel tile,
        HotkeyGesture? proposed)
    {
        if (hotkeyService is null)
        {
            ShowUiError("The global-hotkey service is not initialized.");
            return;
        }

        var target = HotkeyTarget.ForSound(tile.Id);
        var conflict = FindLocalHotkeyConflict(target, proposed);
        if (conflict is not null)
        {
            ShowUiError(conflict);
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        var previous = tile.Sound.Hotkey;
        try
        {
            if (!hotkeyService.TryReplaceBinding(
                    target,
                    proposed,
                    out var registrationError))
            {
                lastHotkeyRegistrationError =
                    registrationError ?? "Unknown registration failure.";
                ShowUiError(lastHotkeyRegistrationError);
                return;
            }

            try
            {
                var updated = await soundLibraryStore.UpdateHotkeyAsync(
                    tile.Id,
                    proposed);
                tile.ReplaceSound(updated);
            }
            catch
            {
                hotkeyService.TryReplaceBinding(
                    target,
                    previous,
                    out var rollbackError);
                if (rollbackError is not null)
                {
                    lastHotkeyRegistrationError = rollbackError;
                }

                throw;
            }

            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = proposed is null
                ? $"Cleared the hotkey for \"{tile.DisplayName}\"."
                : $"Assigned {proposed.DisplayText} to "
                    + $"\"{tile.DisplayName}\" and registered it with "
                    + "Windows.";
            lastHotkeyAction = proposed is null
                ? $"Cleared hotkey for sound {tile.Id}"
                : $"Assigned {proposed.DisplayText} to sound {tile.Id}";
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The sound hotkey could not be saved: "
                + exception.Message);
        }
        finally
        {
            libraryActionGate.Release();
            UpdateHotkeyPresentation();
        }
    }

    private async void AssignStopHotkeyButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new HotkeyAssignmentDialog(
            "Application action: Stop current sound",
            appSettings.StopSoundHotkey)
        {
            Owner = this
        };
        if (!ShowHotkeyAssignmentDialog(dialog))
        {
            return;
        }

        await ApplyStopHotkeyAsync(
            dialog.ClearRequested
                ? null
                : dialog.ProposedHotkey);
    }

    private bool ShowHotkeyAssignmentDialog(
        HotkeyAssignmentDialog dialog)
    {
        var restoreRegistrations = hotkeyService?.Enabled == true;
        if (restoreRegistrations)
        {
            hotkeyService!.SetEnabled(false);
            UpdateHotkeyPresentation();
        }

        var accepted = false;
        try
        {
            accepted = dialog.ShowDialog() == true;
        }
        finally
        {
            if (restoreRegistrations
                && !isClosing
                && hotkeyService is not null)
            {
                hotkeyService.SetEnabled(true);
                UpdateHotkeyPresentation();
            }
        }

        return accepted;
    }

    private async void ClearStopHotkeyButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await ApplyStopHotkeyAsync(null);
    }

    private async Task ApplyStopHotkeyAsync(HotkeyGesture? proposed)
    {
        if (hotkeyService is null)
        {
            ShowUiError("The global-hotkey service is not initialized.");
            return;
        }

        var conflict = FindLocalHotkeyConflict(
            HotkeyTarget.StopSound,
            proposed);
        if (conflict is not null)
        {
            ShowUiError(conflict);
            return;
        }

        var previous = appSettings.StopSoundHotkey;
        if (!hotkeyService.TryReplaceBinding(
                HotkeyTarget.StopSound,
                proposed,
                out var registrationError))
        {
            lastHotkeyRegistrationError =
                registrationError ?? "Unknown registration failure.";
            ShowUiError(lastHotkeyRegistrationError);
            UpdateHotkeyPresentation();
            return;
        }

        try
        {
            var updatedSettings = appSettings with
            {
                StopSoundHotkey = proposed
            };
            settingsSaveDelayCancellation?.Cancel();
            settingsSaveDelayCancellation?.Dispose();
            settingsSaveDelayCancellation = null;
            await settingsStore.SaveAsync(updatedSettings);
            appSettings = updatedSettings;
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = proposed is null
                ? "Cleared the Stop Sound hotkey."
                : $"Assigned {proposed.DisplayText} to Stop Sound and "
                    + "registered it with Windows.";
            lastHotkeyAction = proposed is null
                ? "Cleared Stop Sound hotkey"
                : $"Assigned {proposed.DisplayText} to Stop Sound";
        }
        catch (Exception exception)
        {
            hotkeyService.TryReplaceBinding(
                HotkeyTarget.StopSound,
                previous,
                out var rollbackError);
            if (rollbackError is not null)
            {
                lastHotkeyRegistrationError = rollbackError;
            }

            ShowUiError(
                $"The Stop Sound hotkey could not be saved: "
                + exception.Message);
        }
        finally
        {
            UpdateHotkeyPresentation();
        }
    }

    private string? FindLocalHotkeyConflict(
        HotkeyTarget target,
        HotkeyGesture? proposed)
    {
        if (proposed is null)
        {
            return null;
        }

        if (target != HotkeyTarget.StopSound
            && appSettings.StopSoundHotkey == proposed)
        {
            return $"{proposed.DisplayText} is already assigned to "
                + "Stop Sound.";
        }

        foreach (var tile in soundTiles)
        {
            if (target.Kind == HotkeyTargetKind.Sound
                && tile.Id == target.SoundId)
            {
                continue;
            }

            if (tile.Sound.Hotkey == proposed)
            {
                return $"{proposed.DisplayText} is already assigned to "
                    + $"\"{tile.DisplayName}\".";
            }
        }

        return null;
    }

    private async void EditTileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        var dialog = new EditSoundDialog(
            tile.Sound,
            soundCategories.ToArray())
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var updated = await soundLibraryStore.UpdateSoundAsync(
                tile.Id,
                new SoundMetadataUpdate(
                    dialog.SoundName,
                    dialog.CategoryId,
                    dialog.IsFavorite,
                    dialog.TileAccent));
            tile.ReplaceSound(
                updated,
                GetCategoryName(updated.CategoryId));
            if (currentSoundId == tile.Id)
            {
                CurrentSoundTextBlock.Text =
                    $"Playing {updated.DisplayName} · one-shot";
            }

            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Saved metadata for \"{updated.DisplayName}\".";
            RefreshDiagnosticStatus();
            FocusAfterLibraryMutation(tile.Id);
        }
        catch (Exception exception)
        {
            ShowUiError($"The sound could not be edited: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void FavoriteTileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var updated = await soundLibraryStore.UpdateSoundAsync(
                tile.Id,
                new SoundMetadataUpdate(
                    tile.DisplayName,
                    tile.Sound.CategoryId,
                    !tile.IsFavorite,
                    tile.Sound.TileAccent));
            tile.ReplaceSound(
                updated,
                GetCategoryName(updated.CategoryId));
            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = updated.IsFavorite
                ? $"Added \"{updated.DisplayName}\" to Favorites."
                : $"Removed \"{updated.DisplayName}\" from Favorites.";
            FocusAfterLibraryMutation(tile.Id);
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Favorite state could not be saved: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void EditClipTileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        var previewEndpoint = GetSafePreviewEndpoint(
            out var previewAvailabilityMessage);
        var dialog = new EditClipDialog(
            tile.Sound,
            soundLibraryStore.GetManagedFilePath(tile.Sound),
            waveformCacheService,
            previewService,
            loudnessAnalysisService,
            previewEndpoint,
            previewAvailabilityMessage,
            appSettings.NormalizationTargetLufs,
            appSettings.SafetyLimiterEnabled,
            appSettings.SafetyLimiterCeilingDbfs)
        {
            Owner = this
        };
        var accepted = dialog.ShowDialog() == true;
        RefreshDiagnosticStatus();
        if (!accepted
            || dialog.ProposedUpdate is not { } proposedUpdate)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            if (audioEngine.CurrentSoundId == tile.Id)
            {
                await Task.Run(audioEngine.StopSound);
            }

            var updated = await soundLibraryStore.UpdateClipSettingsAsync(
                tile.Id,
                proposedUpdate);
            tile.ReplaceSound(
                updated,
                GetCategoryName(updated.CategoryId));
            if (dialog.SavedAnalysisOutcome is { } savedAnalysis)
            {
                tile.SetLoudnessAnalysis(
                    savedAnalysis.Key,
                    savedAnalysis.Result);
            }
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Saved non-destructive clip settings for "
                + $"\"{updated.DisplayName}\". Playback was not restarted.";
            RefreshDiagnosticStatus();
            FocusAfterLibraryMutation(tile.Id);
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Clip settings could not be saved: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void RemoveTileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Remove \"{tile.DisplayName}\" from the soundboard and "
            + "delete its managed copy? The original imported file is "
            + "not affected.",
            "Remove sound",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            previewService.Stop();
            var visibleTiles = soundTilesView
                .Cast<SoundTileViewModel>()
                .ToList();
            var removedVisibleIndex = visibleTiles.IndexOf(tile);
            var focusSoundId = visibleTiles
                .Where(candidate => candidate.Id != tile.Id)
                .ElementAtOrDefault(
                    Math.Max(
                        0,
                        Math.Min(
                            removedVisibleIndex,
                            visibleTiles.Count - 2)))
                ?.Id;

            if (audioEngine.CurrentSoundId == tile.Id)
            {
                await Task.Run(audioEngine.StopSound);
            }

            var target = HotkeyTarget.ForSound(tile.Id);
            var removedHotkey = tile.Sound.Hotkey;
            hotkeyService?.RemoveBinding(target);
            try
            {
                var waveformWarnings =
                    await soundLibraryStore.RemoveAsync(tile.Id);
                ErrorTextBlock.Text = string.Join(
                    Environment.NewLine,
                    waveformWarnings);
            }
            catch
            {
                if (removedHotkey is not null)
                {
                    hotkeyService?.LoadPersistedBinding(
                        target,
                        removedHotkey);
                }

                throw;
            }

            soundTiles.Remove(tile);
            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            StatusTextBlock.Text =
                $"Removed \"{tile.DisplayName}\" and its managed copy.";
            lastHotkeyAction =
                $"Removed binding for sound {tile.Id} before removal";
            RefreshDiagnosticStatus();
            if (focusSoundId is { } nextSoundId)
            {
                FocusAfterLibraryMutation(nextSoundId);
            }
            else
            {
                LibraryViewsListBox.Focus();
            }
        }
        catch (Exception exception)
        {
            ShowUiError($"The sound could not be removed: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
            UpdateHotkeyPresentation();
        }
    }

    private async void CreateCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new CategoryNameDialog(
            "Create category",
            "Create category")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var category = await soundLibraryStore.CreateCategoryAsync(
                dialog.CategoryName);
            soundCategories.Add(category);
            RebuildLibraryViews(
                SoundLibraryViewKind.Category,
                category.Id);
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Created category \"{category.DisplayName}\".";
            UpdateLibraryPresentation();
            LibraryViewsListBox.Focus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The category could not be created: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void RenameCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SelectedLibraryView is not
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            } selected)
        {
            return;
        }

        var dialog = new CategoryNameDialog(
            "Rename category",
            "Rename category",
            selected.DisplayName)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var renamed = await soundLibraryStore.RenameCategoryAsync(
                categoryId,
                dialog.CategoryName);
            var index = soundCategories
                .Select((category, itemIndex) => (category, itemIndex))
                .First(item => item.category.Id == categoryId)
                .itemIndex;
            soundCategories[index] = renamed;
            foreach (var tile in soundTiles.Where(
                         tile => tile.Sound.CategoryId == categoryId))
            {
                tile.SetCategoryName(renamed.DisplayName);
            }

            RebuildLibraryViews(
                SoundLibraryViewKind.Category,
                categoryId);
            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Renamed category to \"{renamed.DisplayName}\".";
            LibraryViewsListBox.Focus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The category could not be renamed: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void DeleteCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SelectedLibraryView is not
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            } selected)
        {
            return;
        }

        var assignedCount = soundTiles.Count(
            tile => tile.Sound.CategoryId == categoryId);
        var confirmation = MessageBox.Show(
            this,
            $"Delete category \"{selected.DisplayName}\"? "
            + $"{assignedCount} visible sound(s) will move to "
            + "Uncategorized. No sounds or managed audio files will be "
            + "deleted.",
            "Delete category",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var result = await soundLibraryStore.DeleteCategoryAsync(
                categoryId);
            ReplaceCategories(result.Categories);
            var soundsById = result.Sounds.ToDictionary(sound => sound.Id);
            foreach (var tile in soundTiles)
            {
                if (soundsById.TryGetValue(tile.Id, out var updated))
                {
                    tile.ReplaceSound(
                        updated,
                        GetCategoryName(updated.CategoryId));
                }
            }

            RebuildLibraryViews(
                SoundLibraryViewKind.Uncategorized,
                categoryId: null);
            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Deleted category \"{selected.DisplayName}\" and moved "
                + $"{result.UncategorizedSoundCount} sound(s) to "
                + "Uncategorized.";
            LibraryViewsListBox.Focus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The category could not be deleted: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private async void MoveCategoryUpButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await MoveSelectedCategoryAsync(-1);
    }

    private async void MoveCategoryDownButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await MoveSelectedCategoryAsync(1);
    }

    private async Task MoveSelectedCategoryAsync(int offset)
    {
        if (SelectedLibraryView is not
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            })
        {
            return;
        }

        var currentIndex = soundCategories
            .Select((category, index) => (category, index))
            .First(item => item.category.Id == categoryId)
            .index;
        var newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= soundCategories.Count)
        {
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var orderedIds = soundCategories
                .Select(category => category.Id)
                .ToList();
            (orderedIds[currentIndex], orderedIds[newIndex]) =
                (orderedIds[newIndex], orderedIds[currentIndex]);
            var reordered =
                await soundLibraryStore.ReorderCategoriesAsync(orderedIds);
            ReplaceCategories(reordered);
            RebuildLibraryViews(
                SoundLibraryViewKind.Category,
                categoryId);
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Moved category {(
                    offset < 0 ? "earlier" : "later")}.";
            LibraryViewsListBox.Focus();
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The category order could not be saved: "
                + exception.Message);
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private void LibraryViewsListBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        soundTilesView?.Refresh();
        UpdateLibraryPresentation();
        UpdateCategoryControlAvailability();
    }

    private void SearchTextBox_TextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        soundTilesView?.Refresh();
        UpdateLibraryPresentation();
    }

    private bool FilterSoundTile(object item)
    {
        if (item is not SoundTileViewModel tile)
        {
            return false;
        }

        var selectedView = SelectedLibraryView
            ?? libraryViews.FirstOrDefault();
        return selectedView is not null
            && SoundLibraryFilter.MatchesView(
                tile.Sound,
                selectedView)
            && SoundLibraryFilter.MatchesSearch(
                tile.Sound,
                tile.CategoryName,
                SearchTextBox?.Text);
    }

    private void SoundDragHandle_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile
            || !CanReorderSounds)
        {
            soundDragStartPoint = null;
            draggedSoundId = null;
            return;
        }

        soundDragStartPoint = eventArgs.GetPosition(this);
        draggedSoundId = tile.Id;
    }

    private void SoundDragHandle_PreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed
            || soundDragStartPoint is not { } startPoint
            || draggedSoundId is not { } soundId
            || !CanReorderSounds)
        {
            return;
        }

        var currentPoint = eventArgs.GetPosition(this);
        if (Math.Abs(currentPoint.X - startPoint.X)
                < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - startPoint.Y)
                < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        soundDragStartPoint = null;
        var data = new DataObject();
        data.SetData(SoundDragDataFormat, soundId.ToString("D"));
        try
        {
            _ = DragDrop.DoDragDrop(
                (DependencyObject)sender,
                data,
                DragDropEffects.Move);
        }
        finally
        {
            draggedSoundId = null;
        }
    }

    private void SoundTile_DragOver(
        object sender,
        DragEventArgs eventArgs)
    {
        eventArgs.Effects =
            CanReorderSounds
            && eventArgs.Data.GetDataPresent(SoundDragDataFormat)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void SoundTile_Drop(
        object sender,
        DragEventArgs eventArgs)
    {
        try
        {
            if (!CanReorderSounds
                || (sender as FrameworkElement)?.Tag
                    is not SoundTileViewModel target
                || eventArgs.Data.GetData(SoundDragDataFormat)
                    is not string sourceText
                || !Guid.TryParse(sourceText, out var sourceId)
                || sourceId == target.Id)
            {
                return;
            }

            var orderedIds = soundTilesView
                .Cast<SoundTileViewModel>()
                .Select(tile => tile.Id)
                .ToList();
            if (!orderedIds.Remove(sourceId))
            {
                return;
            }

            var targetIndex = orderedIds.IndexOf(target.Id);
            if (targetIndex < 0)
            {
                return;
            }

            if (eventArgs.GetPosition((IInputElement)sender).Y
                > ((FrameworkElement)sender).ActualHeight / 2)
            {
                targetIndex++;
            }

            orderedIds.Insert(targetIndex, sourceId);
            eventArgs.Handled = true;
            await PersistVisibleSoundOrderAsync(
                orderedIds,
                sourceId);
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The sound order could not be changed: "
                + exception.Message);
        }
    }

    private async void MoveSoundEarlierMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await MoveSoundByKeyboardAsync(sender, -1);
    }

    private async void MoveSoundLaterMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await MoveSoundByKeyboardAsync(sender, 1);
    }

    private async Task MoveSoundByKeyboardAsync(
        object sender,
        int offset)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile
            || !CanReorderSounds)
        {
            return;
        }

        var orderedIds = soundTilesView
            .Cast<SoundTileViewModel>()
            .Select(item => item.Id)
            .ToList();
        var currentIndex = orderedIds.IndexOf(tile.Id);
        var newIndex = currentIndex + offset;
        if (currentIndex < 0
            || newIndex < 0
            || newIndex >= orderedIds.Count)
        {
            return;
        }

        (orderedIds[currentIndex], orderedIds[newIndex]) =
            (orderedIds[newIndex], orderedIds[currentIndex]);
        await PersistVisibleSoundOrderAsync(orderedIds, tile.Id);
    }

    private async Task PersistVisibleSoundOrderAsync(
        IReadOnlyList<Guid> orderedIds,
        Guid focusSoundId)
    {
        if (!CanReorderSounds)
        {
            ShowUiError(ReorderAvailabilityText);
            return;
        }

        if (!await libraryActionGate.WaitAsync(0))
        {
            ShowUiError(
                "Another library operation is already in progress.");
            return;
        }

        try
        {
            var selectedView = SelectedLibraryView
                ?? throw new InvalidOperationException(
                    "Select a library view before reordering.");
            var persisted = await soundLibraryStore.ReorderSoundsAsync(
                orderedIds,
                selectedView.CategoryId,
                selectedView.Kind == SoundLibraryViewKind.AllSounds);
            ApplyPersistedSoundOrder(persisted);
            soundTilesView.Refresh();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = "Saved the manual sound order.";
            FocusAfterLibraryMutation(focusSoundId);
        }
        catch (Exception exception)
        {
            ShowUiError(
                "The sound order could not be saved. The previous visible "
                + $"order was restored: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private void AudioEngine_StateChanged(
        object? sender,
        AudioEngineStateChangedEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                UpdateEnginePresentation();
                UpdateControlAvailability();
                RefreshDiagnosticStatus();
            });
    }

    /// <summary>
    /// Renders the engine state in the top bar. State is carried by text
    /// and by a distinct glyph as well as colour, so the indicator stays
    /// readable without colour perception.
    /// </summary>
    private void UpdateEnginePresentation()
    {
        var state = audioEngine.State;
        EngineStateTextBlock.Text = state.ToString();
        var (glyphKey, brushKey) = state switch
        {
            AudioEngineState.Running =>
                ("GlyphPlay", "SuccessBrush"),
            AudioEngineState.Starting =>
                ("GlyphRestart", "WarningBrush"),
            AudioEngineState.Stopping =>
                ("GlyphRestart", "WarningBrush"),
            AudioEngineState.Faulted =>
                ("GlyphWarning", "ErrorBrush"),
            _ => ("GlyphStop", "TextMutedBrush")
        };
        EngineStateGlyph.Text = ThemeGlyph(glyphKey);
        EngineStateGlyph.Foreground = ThemeBrush(brushKey);
        EngineStatePill.BorderBrush = state switch
        {
            AudioEngineState.Running => ThemeBrush("SuccessBrush"),
            AudioEngineState.Faulted => ThemeBrush("ErrorBrush"),
            _ => ThemeBrush("BorderStrongBrush")
        };

        var micMuted = MuteMicrophoneCheckBox.IsChecked == true;
        MicrophoneStateTextBlock.Text = state switch
        {
            AudioEngineState.Running when micMuted =>
                "Microphone muted",
            AudioEngineState.Running => "Microphone live",
            AudioEngineState.Faulted => "Microphone released",
            _ => "Microphone idle"
        };
        MicrophoneStateTextBlock.Foreground = micMuted
            && state == AudioEngineState.Running
                ? ThemeBrush("WarningBrush")
                : ThemeBrush("TextMutedBrush");
    }

    /// <summary>
    /// Keeps the compact bottom-bar chips current: monitoring state, the
    /// most recent global-hotkey action, and limiter activity, which only
    /// appears while the limiter is actually reducing gain.
    /// </summary>
    private void UpdateStatusChips()
    {
        MonitoringStatusTextBlock.Text =
            MonitorSoundsCheckBox.IsChecked == true
                ? audioEngine.State == AudioEngineState.Running
                    && audioEngine.Diagnostics?.MonitorInitializationStatus
                        == "Ready"
                    ? "Monitor: on"
                    : "Monitor: requested"
                : "Monitor: off";

        HotkeyActionTextBlock.Text = $"Hotkey: {lastHotkeyAction}";

        var diagnostics = audioEngine.Diagnostics;
        var reduction = Math.Max(
            diagnostics?.VirtualLimiterCurrentGainReductionDb ?? 0f,
            diagnostics?.MonitorLimiterCurrentGainReductionDb ?? 0f);
        if (appSettings.SafetyLimiterEnabled && reduction >= 0.1f)
        {
            LimiterActivityTextBlock.Text =
                $"Limiting {reduction:N1} dB";
            LimiterActivityChip.Visibility = Visibility.Visible;
        }
        else
        {
            LimiterActivityChip.Visibility = Visibility.Collapsed;
        }
    }

    private void AudioEngine_ErrorOccurred(
        object? sender,
        AudioEngineErrorEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                lastDiagnosticMessage = eventArgs.Message;
                ErrorTextBlock.Text = eventArgs.Message;

                if (!eventArgs.IsRecoverable)
                {
                    StatusTextBlock.Text =
                        "The audio engine encountered a device or stream "
                        + "error.";
                }

                UpdateMonitorStatusForSelection();
                RefreshDiagnosticStatus();
            });
    }

    private void AudioEngine_PeakLevelsChanged(
        object? sender,
        AudioPeakLevelsEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                MicrophonePeakProgressBar.Value =
                    Math.Clamp(eventArgs.MicrophonePeak, 0f, 1f);
                OutputPeakProgressBar.Value =
                    Math.Clamp(eventArgs.MixedOutputPeak, 0f, 1f);
                MicrophonePeakTextBlock.Text =
                    $"{eventArgs.MicrophonePeak:P0}";
                OutputPeakTextBlock.Text =
                    $"{eventArgs.MixedOutputPeak:P0}";
                MonitorPeakProgressBar.Value =
                    Math.Clamp(eventArgs.MonitorOutputPeak, 0f, 1f);
                MonitorPeakTextBlock.Text =
                    $"{eventArgs.MonitorOutputPeak:P0}";
                UpdateLoudnessAndLimiterPresentation();
            });
    }

    private void AudioEngine_SoundPlaybackStateChanged(
        object? sender,
        SoundPlaybackStateChangedEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                if (eventArgs.Reason == SoundPlaybackChangeReason.Started)
                {
                    if (eventArgs.SessionId < currentSoundSessionId)
                    {
                        return;
                    }

                    currentSoundSessionId = eventArgs.SessionId;
                    currentSoundId = eventArgs.SoundId;
                    SetPlayingTile(eventArgs.SoundId);
                    var playingName = FindTile(eventArgs.SoundId)?.DisplayName
                        ?? eventArgs.SoundId.ToString();
                    CurrentSoundTextBlock.Text =
                        $"Playing {playingName} · one-shot";
                }
                else if (eventArgs.SessionId == currentSoundSessionId)
                {
                    var finishedName =
                        FindTile(eventArgs.SoundId)?.DisplayName
                        ?? "Sound";
                    currentSoundId = null;
                    SetPlayingTile(null);
                    CurrentSoundTextBlock.Text = "No sound playing";

                    if (audioEngine.State == AudioEngineState.Running)
                    {
                        StatusTextBlock.Text = eventArgs.Reason switch
                        {
                            SoundPlaybackChangeReason.Completed =>
                                $"{finishedName} finished naturally. The "
                                + "microphone remains active.",
                            SoundPlaybackChangeReason.Stopped =>
                                "Sound playback stopped. The microphone "
                                + "remains active.",
                            _ => StatusTextBlock.Text
                        };
                    }
                }

                UpdateControlAvailability();
                RefreshDiagnosticStatus();
            });
    }

    private void SetPlayingTile(Guid? soundId)
    {
        foreach (var tile in soundTiles)
        {
            tile.IsPlaying = tile.Id == soundId;
        }
    }

    private SoundTileViewModel? FindTile(Guid soundId)
    {
        return soundTiles.FirstOrDefault(tile => tile.Id == soundId);
    }

    private LibraryViewItem? SelectedLibraryView =>
        LibraryViewsListBox?.SelectedItem as LibraryViewItem;

    private bool CanReorderSounds
    {
        get
        {
            return SoundLibraryFilter.CanReorder(
                SelectedLibraryView,
                SearchTextBox?.Text);
        }
    }

    private string ReorderAvailabilityText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SearchTextBox?.Text))
            {
                return "Clear search to reorder sounds.";
            }

            if (SelectedLibraryView?.Kind
                == SoundLibraryViewKind.Favorites)
            {
                return "Sound ordering is unavailable in Favorites. "
                    + "Choose All Sounds, Uncategorized, or a category.";
            }

            return "Drag the handle or use Move earlier/Move later.";
        }
    }

    private string GetCategoryName(Guid? categoryId)
    {
        return categoryId is null
            ? "Uncategorized"
            : soundCategories.FirstOrDefault(
                    category => category.Id == categoryId)
                ?.DisplayName
                ?? "Uncategorized";
    }

    private void RebuildLibraryViews(
        SoundLibraryViewKind preferredKind,
        Guid? categoryId)
    {
        libraryViews.Clear();
        libraryViews.Add(
            new LibraryViewItem(
                SoundLibraryViewKind.AllSounds,
                "All Sounds"));
        libraryViews.Add(
            new LibraryViewItem(
                SoundLibraryViewKind.Favorites,
                "Favorites"));
        libraryViews.Add(
            new LibraryViewItem(
                SoundLibraryViewKind.Uncategorized,
                "Uncategorized"));
        foreach (var category in soundCategories
                     .OrderBy(category => category.SortOrder))
        {
            libraryViews.Add(
                new LibraryViewItem(
                    SoundLibraryViewKind.Category,
                    category.DisplayName,
                    category.Id));
        }

        var selected = libraryViews.FirstOrDefault(
                view =>
                    view.Kind == preferredKind
                    && view.CategoryId == categoryId)
            ?? libraryViews[0];
        LibraryViewsListBox.SelectedItem = selected;
        UpdateCategoryControlAvailability();
    }

    private void ReplaceCategories(
        IEnumerable<SoundCategory> replacements)
    {
        soundCategories.Clear();
        foreach (var category in replacements
                     .OrderBy(category => category.SortOrder))
        {
            soundCategories.Add(category);
        }
    }

    private void ApplyPersistedSoundOrder(
        IReadOnlyList<SoundLibraryEntry> persistedSounds)
    {
        var persistedById = persistedSounds.ToDictionary(sound => sound.Id);
        foreach (var tile in soundTiles)
        {
            if (persistedById.TryGetValue(tile.Id, out var persisted))
            {
                tile.ReplaceSound(
                    persisted,
                    GetCategoryName(persisted.CategoryId));
            }
        }

        var desired = soundTiles
            .OrderBy(tile => tile.Sound.SortOrder)
            .ToList();
        for (var index = 0; index < desired.Count; index++)
        {
            var currentIndex = soundTiles.IndexOf(desired[index]);
            if (currentIndex != index)
            {
                soundTiles.Move(currentIndex, index);
            }
        }
    }

    private void UpdateCategoryControlAvailability()
    {
        if (RenameCategoryMenuItem is null)
        {
            return;
        }

        var categoryId = SelectedLibraryView?.Kind
            == SoundLibraryViewKind.Category
                ? SelectedLibraryView.CategoryId
                : null;
        var index = categoryId is null
            ? -1
            : soundCategories
                .Select((category, itemIndex) => (category, itemIndex))
                .Where(item => item.category.Id == categoryId)
                .Select(item => item.itemIndex)
                .DefaultIfEmpty(-1)
                .First();
        RenameCategoryMenuItem.IsEnabled = categoryId is not null;
        DeleteCategoryMenuItem.IsEnabled = categoryId is not null;
        MoveCategoryUpMenuItem.IsEnabled = index > 0;
        MoveCategoryDownMenuItem.IsEnabled =
            index >= 0 && index < soundCategories.Count - 1;
        ManageCategoriesButton.IsEnabled = categoryId is not null;
        ManageCategoriesButton.ToolTip = categoryId is null
            ? "Select a user category to rename, reorder, or delete it"
            : "Manage the selected category";
    }

    private void FocusAfterLibraryMutation(Guid preferredSoundId)
    {
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                var tile = FindTile(preferredSoundId);
                if (tile is not null && FilterSoundTile(tile))
                {
                    if (SoundTilesItemsControl.ItemContainerGenerator
                            .ContainerFromItem(tile)
                        is UIElement container)
                    {
                        container.MoveFocus(
                            new TraversalRequest(
                                FocusNavigationDirection.First));
                        return;
                    }
                }

                LibraryViewsListBox.Focus();
            });
    }

    private void UpdateControlAvailability()
    {
        var state = audioEngine.State;
        var selectorsCanChange =
            state == AudioEngineState.Stopped && !isRefreshing;
        var selectedMicrophone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var selectedRender =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        var relatedVbCapture = currentSnapshot?.CaptureEndpoints
            .Any(endpoint => endpoint.IsLikelyVbCable) == true;

        MicrophoneComboBox.IsEnabled = selectorsCanChange;
        VirtualOutputComboBox.IsEnabled = selectorsCanChange;
        MonitorSoundsCheckBox.IsEnabled = selectorsCanChange;
        MonitorOutputComboBox.IsEnabled = selectorsCanChange;
        RefreshDevicesButton.IsEnabled = selectorsCanChange;
        StartEngineButton.IsEnabled =
            selectorsCanChange
            && selectedMicrophone is not null
            && selectedRender?.IsLikelyVbCable == true
            && relatedVbCapture;
        StopEngineButton.IsEnabled =
            state is AudioEngineState.Starting
                or AudioEngineState.Running
                or AudioEngineState.Faulted;

        var volumeControlsEnabled =
            state is AudioEngineState.Stopped or AudioEngineState.Running;
        MicrophoneVolumeSlider.IsEnabled = volumeControlsEnabled;
        MuteMicrophoneCheckBox.IsEnabled = volumeControlsEnabled;
        SoundVolumeSlider.IsEnabled = volumeControlsEnabled;
        MonitorVolumeSlider.IsEnabled = volumeControlsEnabled;
        ImportSoundsButton.IsEnabled =
            !isImporting
            && state is not (
                AudioEngineState.Starting
                or AudioEngineState.Stopping);
        StopSoundButton.IsEnabled =
            state == AudioEngineState.Running
            && audioEngine.IsSoundPlaying;
        UpdateCategoryControlAvailability();
        UpdateMonitorStatusForSelection();
        UpdateStatusChips();
    }

    private void UpdateLibraryPresentation()
    {
        var visibleCount = soundTilesView?.Cast<object>().Count()
            ?? soundTiles.Count;
        var selectedView = SelectedLibraryView
            ?? libraryViews.FirstOrDefault();
        SoundCountTextBlock.Text = visibleCount == soundTiles.Count
            ? $"{visibleCount} sound{(visibleCount == 1 ? "" : "s")}"
            : $"{visibleCount} of {soundTiles.Count} sounds";
        SelectedViewTextBlock.Text =
            selectedView?.DisplayName ?? "All Sounds";

        UpdateSidebarCounts();
        UpdateEmptyState(selectedView, visibleCount);

        var canReorder = CanReorderSounds;
        var availabilityText = ReorderAvailabilityText;
        ReorderHelpTextBlock.Text = availabilityText;
        foreach (var tile in soundTiles)
        {
            tile.CanReorder = canReorder;
            tile.ReorderAvailabilityText = availabilityText;
        }
    }

    private void UpdateSidebarCounts()
    {
        foreach (var view in libraryViews)
        {
            view.SoundCount = soundTiles.Count(
                tile => SoundLibraryFilter.MatchesView(tile.Sound, view));
        }
    }

    /// <summary>
    /// Chooses the empty-state copy and the single most useful next
    /// action for the current view. Raw exception text is never shown
    /// here; failures stay on the status line.
    /// </summary>
    private void UpdateEmptyState(
        LibraryViewItem? selectedView,
        int visibleCount)
    {
        EmptyStatePanel.Visibility = visibleCount == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SoundGridScrollViewer.Visibility = visibleCount == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (visibleCount != 0)
        {
            return;
        }

        var searching = !string.IsNullOrWhiteSpace(SearchTextBox?.Text);
        if (isImporting)
        {
            emptyStateAction = EmptyStateAction.None;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphRestart");
            EmptyStateTitleTextBlock.Text = "Importing…";
            EmptyStateMessageTextBlock.Text =
                "Files are being inspected and copied into the managed "
                + "library. This does not modify the originals.";
        }
        else if (soundTiles.Count == 0)
        {
            emptyStateAction = EmptyStateAction.Import;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphVolume");
            EmptyStateTitleTextBlock.Text = "No sounds yet";
            EmptyStateMessageTextBlock.Text =
                "Import a few clips to build your soundboard. Sounds are "
                + "copied into a local library, so the originals stay "
                + "where they are.";
        }
        else if (searching)
        {
            emptyStateAction = EmptyStateAction.ClearSearch;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphSearch");
            EmptyStateTitleTextBlock.Text = "No matches";
            EmptyStateMessageTextBlock.Text =
                $"Nothing in {selectedView?.DisplayName ?? "this view"} "
                + $"matches “{SearchTextBox!.Text.Trim()}”. "
                + "Search covers sound names, original filenames, and "
                + "category names.";
        }
        else if (selectedView?.Kind == SoundLibraryViewKind.Favorites)
        {
            emptyStateAction = EmptyStateAction.ShowAll;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphFavoriteOn");
            EmptyStateTitleTextBlock.Text = "No favorites yet";
            EmptyStateMessageTextBlock.Text =
                "Select the star on any tile to keep that sound here for "
                + "quick access.";
        }
        else
        {
            emptyStateAction = EmptyStateAction.Import;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphVolume");
            EmptyStateTitleTextBlock.Text =
                $"Nothing in {selectedView?.DisplayName ?? "this view"}";
            EmptyStateMessageTextBlock.Text =
                "Import sounds here, or move existing sounds into this "
                + "category from a tile's Edit sound action.";
        }

        EmptyStateActionButton.Content = emptyStateAction switch
        {
            EmptyStateAction.Import => "Import Sounds",
            EmptyStateAction.ClearSearch => "Clear search",
            EmptyStateAction.ShowAll => "Show all sounds",
            _ => string.Empty
        };
        EmptyStateActionButton.Visibility =
            emptyStateAction == EmptyStateAction.None
                ? Visibility.Collapsed
                : Visibility.Visible;
        EmptyStateFormatsTextBlock.Visibility =
            emptyStateAction == EmptyStateAction.Import
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void EmptyStateActionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        switch (emptyStateAction)
        {
            case EmptyStateAction.Import:
                ImportSoundsButton_Click(sender, eventArgs);
                break;
            case EmptyStateAction.ClearSearch:
                SearchTextBox.Clear();
                SearchTextBox.Focus();
                break;
            case EmptyStateAction.ShowAll:
                LibraryViewsListBox.SelectedItem = libraryViews
                    .FirstOrDefault(
                        view =>
                            view.Kind == SoundLibraryViewKind.AllSounds);
                LibraryViewsListBox.Focus();
                break;
        }
    }

    private void UpdateHotkeyPresentation()
    {
        if (hotkeyService is null)
        {
            RegisteredHotkeyCountTextBlock.Text = "0 registered";
            StopHotkeyDisplayTextBlock.Text =
                appSettings.StopSoundHotkey?.DisplayText ?? "No hotkey";
            StopHotkeyStateTextBlock.Text = appSettings.StopSoundHotkey is null
                ? "Not assigned"
                : "Assigned · registration pending";
            ClearStopHotkeyButton.IsEnabled =
                appSettings.StopSoundHotkey is not null;
            RetryHotkeysButton.IsEnabled = false;
            return;
        }

        foreach (var tile in soundTiles)
        {
            tile.ApplyHotkeyStatus(
                hotkeyService.GetStatus(
                    HotkeyTarget.ForSound(tile.Id)));
        }

        var statuses = hotkeyService.Statuses;
        var soundStatuses = statuses
            .Where(
                status =>
                    status.Target.Kind == HotkeyTargetKind.Sound)
            .ToArray();
        var assignedSoundCount = soundTiles.Count(
            tile => tile.Sound.Hotkey is not null);
        var registeredSoundCount = soundStatuses.Count(
            status =>
                status.State == HotkeyRegistrationState.Registered);
        var unavailableSoundCount = soundStatuses.Count(
            status =>
                status.State == HotkeyRegistrationState.Unavailable);
        var registeredTotal = statuses.Count(
            status =>
                status.State == HotkeyRegistrationState.Registered);
        var unavailableTotal = statuses.Count(
            status =>
                status.State == HotkeyRegistrationState.Unavailable);

        RegisteredHotkeyCountTextBlock.Text =
            $"{registeredTotal} registered";
        RetryHotkeysButton.IsEnabled =
            hotkeyService.Enabled && unavailableTotal > 0;

        var stopStatus =
            hotkeyService.GetStatus(HotkeyTarget.StopSound);
        StopHotkeyDisplayTextBlock.Text =
            stopStatus.Hotkey?.DisplayText ?? "No hotkey";
        StopHotkeyStateTextBlock.Text = stopStatus.State switch
        {
            HotkeyRegistrationState.Registered =>
                "Assigned · registered",
            HotkeyRegistrationState.Unavailable =>
                "Assigned · unavailable",
            HotkeyRegistrationState.Disabled =>
                "Assigned · global hotkeys disabled",
            _ => "Not assigned"
        };
        StopHotkeyStateTextBlock.ToolTip = stopStatus.Error;
        ClearStopHotkeyButton.IsEnabled = stopStatus.Hotkey is not null;

        HotkeyStatusTextBlock.Text = hotkeyService.Enabled
            ? $"{assignedSoundCount} sound hotkey(s) assigned; "
                + $"{registeredSoundCount} registered; "
                + $"{unavailableSoundCount} unavailable. Stop Sound: "
                + StopHotkeyStateTextBlock.Text + "."
            : $"{assignedSoundCount} sound hotkey(s) assigned and preserved. "
                + "Global registration is disabled.";

        if (hotkeyService.LastRegistrationError is { } registrationError)
        {
            lastHotkeyRegistrationError = registrationError;
        }

        RefreshDiagnosticStatus();
    }

    private void UpdateVirtualCableStatus(AudioDeviceSnapshot snapshot)
    {
        var likelyRender = snapshot.RenderEndpoints
            .FirstOrDefault(
                endpoint =>
                    endpoint.IsLikelyVbCable
                    && endpoint.FriendlyName.Contains(
                        "CABLE Input",
                        StringComparison.OrdinalIgnoreCase))
            ?? snapshot.RenderEndpoints
                .FirstOrDefault(endpoint => endpoint.IsLikelyVbCable);
        var likelyCapture = snapshot.CaptureEndpoints
            .FirstOrDefault(endpoint => endpoint.IsLikelyVbCable);

        if (likelyRender is null || likelyCapture is null)
        {
            VirtualCableStatusTextBlock.Text =
                "A complete VB-CABLE render/capture pair was not detected.";
            VirtualCableStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            return;
        }

        VirtualCableStatusTextBlock.Text =
            $"VB-CABLE detected: \"{likelyRender.FriendlyName}\" → "
            + $"\"{likelyCapture.FriendlyName}\".";
        VirtualCableStatusTextBlock.Foreground = ThemeBrush("SuccessBrush");
    }

    private void UpdateRoutingStatusForSelection()
    {
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;

        if (render is null)
        {
            return;
        }

        if (!render.IsLikelyVbCable)
        {
            StatusTextBlock.Text =
                $"\"{render.FriendlyName}\" is a physical or unrecognized "
                + "render endpoint. Starting is blocked to prevent loud "
                + "microphone feedback.";
            ErrorTextBlock.Text =
                "Select a likely VB-CABLE render endpoint such as "
                + "\"CABLE Input\".";
            return;
        }

        ErrorTextBlock.Text = string.Empty;
        StatusTextBlock.Text =
            $"Selected virtual render endpoint: {render.FriendlyName}";
        UpdateMonitorStatusForSelection();
        UpdateRoutingExplanation();
    }

    private void UpdateMonitorStatusForSelection()
    {
        var monitor =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        var virtualOutput =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;

        if (audioEngine.State == AudioEngineState.Running)
        {
            var runningDiagnostics = audioEngine.Diagnostics;
            if (runningDiagnostics?.MonitorInitializationStatus == "Ready")
            {
                MonitorStatusTextBlock.Text =
                    $"Monitoring soundboard audio only through "
                    + $"{runningDiagnostics.MonitorFriendlyName}.";
                MonitorStatusTextBlock.Foreground = ThemeBrush("SuccessBrush");
            }
            else if (MonitorSoundsCheckBox.IsChecked == true)
            {
                MonitorStatusTextBlock.Text =
                    runningDiagnostics?.LastMonitorWarningOrError
                    ?? "Monitoring is unavailable for this engine session.";
                MonitorStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            }
            else
            {
                MonitorStatusTextBlock.Text =
                    "Monitoring is disabled. No physical render device was opened.";
                MonitorStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
            }

            return;
        }

        if (MonitorSoundsCheckBox.IsChecked != true)
        {
            MonitorStatusTextBlock.Text =
                "Monitoring is disabled. The physical output will not be opened.";
            MonitorStatusTextBlock.Foreground = ThemeBrush("TextMutedBrush");
            return;
        }

        if (monitor is null)
        {
            MonitorStatusTextBlock.Text =
                "Select an active physical headphone or speaker output. The "
                + "virtual microphone can still start without monitoring.";
            MonitorStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            return;
        }

        if (monitor.IsLikelyVbCable
            || string.Equals(
                monitor.DeviceId,
                virtualOutput?.DeviceId,
                StringComparison.Ordinal))
        {
            MonitorStatusTextBlock.Text =
                "Monitoring is blocked because the selected endpoint is the "
                + "virtual cable. Select physical headphones or speakers.";
            MonitorStatusTextBlock.Foreground = ThemeBrush("ErrorBrush");
            return;
        }

        MonitorStatusTextBlock.Text =
            $"Ready to monitor soundboard audio only through "
            + $"{monitor.FriendlyName}. The engine will not restart "
            + "automatically.";
        MonitorStatusTextBlock.Foreground = ThemeBrush("SuccessBrush");
    }

    private void UpdateRoutingExplanation()
    {
        var virtualOutput =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        var monitor =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        VirtualRoutingTextBlock.Text =
            "Microphone + Soundboard → "
            + (virtualOutput?.FriendlyName ?? "select VB-CABLE output");
        MonitorRoutingTextBlock.Text =
            "Soundboard only → "
            + (monitor?.FriendlyName ?? "select a physical output");
    }

    private void RefreshDiagnosticStatus()
    {
        var hotkeyStatuses = hotkeyService?.Statuses
            ?? [];
        var soundHotkeyStatuses = hotkeyStatuses
            .Where(
                status =>
                    status.Target.Kind == HotkeyTargetKind.Sound)
            .ToArray();
        var stopHotkeyStatus = hotkeyService?.GetStatus(
            HotkeyTarget.StopSound);

        var lines = new List<string>
        {
            $"Engine state: {audioEngine.State}",
            $"Library storage: {soundLibraryStore.RootPath}",
            $"Library sound count: {soundTiles.Count}",
            $"Current playing sound: "
                + $"{FindTile(currentSoundId ?? Guid.Empty)?.DisplayName
                    ?? "None"}",
            $"Current playback session ID: "
                + $"{(currentSoundId is null
                    ? "None"
                    : currentSoundSessionId)}",
            $"Monitoring enabled setting: "
                + $"{YesNo(MonitorSoundsCheckBox.IsChecked == true)}",
            $"Global hotkeys enabled: "
                + $"{YesNo(appSettings.GlobalHotkeysEnabled)}",
            $"Normalization target: "
                + $"{appSettings.NormalizationTargetLufs:N1} LUFS",
            $"Safety limiter enabled: "
                + $"{YesNo(appSettings.SafetyLimiterEnabled)}",
            $"Safety limiter ceiling: "
                + $"{appSettings.SafetyLimiterCeilingDbfs:N1} dBFS",
            $"Assigned sound hotkeys: "
                + $"{soundTiles.Count(tile => tile.Sound.Hotkey is not null)}",
            $"Registered sound hotkeys: "
                + $"{soundHotkeyStatuses.Count(
                    status =>
                        status.State
                        == HotkeyRegistrationState.Registered)}",
            $"Unavailable sound hotkeys: "
                + $"{soundHotkeyStatuses.Count(
                    status =>
                        status.State
                        == HotkeyRegistrationState.Unavailable)}",
            $"Stop Sound hotkey state: "
                + $"{stopHotkeyStatus?.State.ToString() ?? "NotAssigned"}"
                + (stopHotkeyStatus?.Hotkey is null
                    ? string.Empty
                    : $" ({stopHotkeyStatus.Hotkey.DisplayText})"),
            $"Last hotkey action: {lastHotkeyAction}",
            $"Last registration error: {lastHotkeyRegistrationError}",
            $"Last triggered sound ID: "
                + $"{lastTriggeredSoundId?.ToString() ?? "None"}",
            $"Last trigger source: "
                + $"{lastSoundTriggerSource?.ToString() ?? "None"}",
            "Windows defaults changed by app: No",
            string.Empty
        };

        var microphone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        var monitor =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        var relatedCapture = currentSnapshot?.CaptureEndpoints
            .FirstOrDefault(endpoint => endpoint.IsLikelyVbCable);

        AddSelectedEndpoint(
            lines,
            "Selected microphone",
            microphone,
            selectedMicrophoneFormat);
        AddSelectedEndpoint(
            lines,
            "Selected virtual render",
            render,
            selectedRenderFormat);
        AddSelectedEndpoint(
            lines,
            "Related VB-CABLE capture",
            relatedCapture,
            format: null);
        AddSelectedEndpoint(
            lines,
            "Selected physical monitor render",
            monitor,
            selectedMonitorFormat);

        var engineDiagnostics = audioEngine.Diagnostics;
        if (engineDiagnostics is not null)
        {
            lines.Add(string.Empty);
            lines.Add("Running pipeline:");
            lines.Add(
                $"- Microphone native: "
                + $"{engineDiagnostics.MicrophoneNativeFormat}");
            lines.Add(
                $"- Render endpoint mix: "
                + $"{engineDiagnostics.RenderMixFormat}");
            lines.Add(
                $"- Mixer target: {engineDiagnostics.MixerTargetFormat}");
            lines.Add(
                $"- Microphone resampling: "
                + $"{YesNo(engineDiagnostics.MicrophoneResamplingActive)}");
            lines.Add(
                $"- Microphone channel conversion: "
                + $"{YesNo(engineDiagnostics.MicrophoneChannelConversionActive)}");
            lines.Add(
                $"- Microphone buffer capacity: "
                + $"{engineDiagnostics.MicrophoneBufferCapacity.TotalMilliseconds:N0} ms");
            lines.Add(
                $"- Safety limiter: "
                + $"{(engineDiagnostics.SafetyLimiterEnabled
                    ? "Enabled"
                    : "Disabled")}");
            lines.Add(
                $"- Limiter ceiling: "
                + $"{engineDiagnostics.SafetyLimiterCeilingDbfs:N1} dBFS");
            lines.Add(
                $"- Limiter lookahead: "
                + $"{engineDiagnostics.SafetyLimiterLookahead.TotalMilliseconds:N1} ms");
            lines.Add(
                $"- Virtual limiter gain reduction current/max: "
                + $"{engineDiagnostics.VirtualLimiterCurrentGainReductionDb:N1}"
                + "/"
                + $"{engineDiagnostics.VirtualLimiterMaximumGainReductionDb:N1} dB");
            lines.Add(
                $"- Monitor limiter gain reduction current/max: "
                + $"{engineDiagnostics.MonitorLimiterCurrentGainReductionDb:N1}"
                + "/"
                + $"{engineDiagnostics.MonitorLimiterMaximumGainReductionDb:N1} dB");
            lines.Add(
                $"- Limiter non-finite samples rejected: "
                + $"{engineDiagnostics.LimiterNonFiniteSampleCount}");
            lines.Add(
                $"- Monitoring enabled for engine session: "
                + $"{YesNo(engineDiagnostics.MonitoringEnabled)}");
            lines.Add(
                $"- Monitor initialization: "
                + $"{engineDiagnostics.MonitorInitializationStatus}");
            lines.Add(
                $"- Monitor endpoint name: "
                + $"{engineDiagnostics.MonitorFriendlyName ?? "None"}");
            lines.Add(
                $"- Monitor endpoint ID: "
                + $"{engineDiagnostics.MonitorEndpointId ?? "None"}");
            lines.Add(
                $"- Monitor endpoint mix: "
                + $"{engineDiagnostics.MonitorMixFormat?.ToString() ?? "N/A"}");
            lines.Add(
                $"- Monitor mixer target: "
                + $"{engineDiagnostics.MonitorTargetFormat?.ToString() ?? "N/A"}");
            lines.Add(
                $"- Monitor sound resampling: "
                + $"{YesNoOrNotActive(
                    engineDiagnostics.MonitorResamplingActive)}");
            lines.Add(
                $"- Monitor sound channel conversion: "
                + $"{YesNoOrNotActive(
                    engineDiagnostics.MonitorChannelConversionActive)}");
            lines.Add(
                $"- Monitor peak: {engineDiagnostics.MonitorPeak:P0}");
            lines.Add(
                $"- Last monitor warning/error: "
                + $"{engineDiagnostics.LastMonitorWarningOrError ?? "None"}");
            lines.Add(
                $"- Current logical sound ID: "
                + $"{engineDiagnostics.CurrentSoundId?.ToString() ?? "None"}");
            lines.Add(
                $"- Current playback session ID: "
                + $"{engineDiagnostics.CurrentPlaybackSessionId?.ToString()
                    ?? "None"}");
        }

        lines.Add(
            $"- Microphone buffer overflows: "
            + $"{audioEngine.MicrophoneBufferOverflowCount}");
        lines.Add(
            $"- Preview limiter gain reduction current/max: "
            + $"{previewService.CurrentGainReductionDb:N1}/"
            + $"{previewService.MaximumGainReductionDb:N1} dB");
        lines.Add(
            $"- Preview limiter non-finite samples rejected: "
            + $"{previewService.NonFiniteSampleCount}");
        lines.Add($"- Last error/diagnostic: {lastDiagnosticMessage}");

        if (currentSnapshot?.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Discovery warnings:");
            lines.AddRange(
                currentSnapshot.Warnings.Select(
                    warning => $"- {warning}"));
        }

        DiagnosticStatusTextBox.Text =
            string.Join(Environment.NewLine, lines);
    }

    private static void AddSelectedEndpoint(
        ICollection<string> lines,
        string label,
        AudioEndpoint? endpoint,
        AudioFormatInfo? format)
    {
        lines.Add($"{label}:");
        if (endpoint is null)
        {
            lines.Add("- None selected");
            return;
        }

        lines.Add($"- Name: {endpoint.FriendlyName}");
        lines.Add($"- Endpoint ID: {endpoint.DeviceId}");
        if (format is not null)
        {
            lines.Add($"- Native/mix format: {format}");
        }
    }

    private static string BuildImportDetails(SoundImportResult result)
    {
        var details = new List<string>();
        details.AddRange(
            result.Imported.Select(
                sound =>
                    $"{sound.OriginalFileName}: imported as "
                    + $"{sound.FormatLabel}."));
        details.AddRange(
            result.Duplicates.Select(
                duplicate =>
                    $"{duplicate.SourceFileName}: duplicate of "
                    + $"\"{duplicate.ExistingDisplayName}\"."));
        details.AddRange(
            result.InvalidFiles.Select(
                failure =>
                    $"{failure.SourceFileName}: invalid - "
                    + failure.Reason));
        details.AddRange(
            result.Errors.Select(
                failure =>
                    $"{failure.SourceFileName}: error - "
                    + failure.Reason));
        return string.Join(Environment.NewLine, details);
    }

    private void UpdateSettingsFromUi()
    {
        if (isApplyingSettings)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        appSettings = appSettings with
        {
            MicrophoneEndpointId =
                (MicrophoneComboBox.SelectedItem as AudioEndpoint)?.DeviceId,
            VirtualOutputEndpointId =
                (VirtualOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId,
            MonitoringEnabled =
                MonitorSoundsCheckBox.IsChecked == true,
            MonitorOutputEndpointId =
                (MonitorOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId,
            MonitorVolume = MonitorVolumeSlider.Value / 100d,
            GlobalHotkeysEnabled =
                GlobalHotkeysCheckBox.IsChecked == true,
            NormalizationTargetLufs =
                NormalizationTargetSlider.Value,
            SafetyLimiterEnabled =
                SafetyLimiterCheckBox.IsChecked == true,
            SafetyLimiterCeilingDbfs =
                Math.Round(LimiterCeilingSlider.Value, 1),
            MicrophoneVolume = MicrophoneVolumeSlider.Value / 100d,
            MicrophoneMuted =
                MuteMicrophoneCheckBox.IsChecked == true,
            SoundVolume = SoundVolumeSlider.Value / 100d,
            WindowLeft = double.IsFinite(bounds.Left)
                ? bounds.Left
                : null,
            WindowTop = double.IsFinite(bounds.Top)
                ? bounds.Top
                : null,
            WindowWidth = bounds.Width >= MinWidth
                ? bounds.Width
                : Width,
            WindowHeight = bounds.Height >= MinHeight
                ? bounds.Height
                : Height,
            WindowMaximized = WindowState == WindowState.Maximized
        };
    }

    private void ScheduleSettingsSave()
    {
        if (isApplyingSettings || isClosing)
        {
            return;
        }

        settingsSaveDelayCancellation?.Cancel();
        settingsSaveDelayCancellation?.Dispose();
        settingsSaveDelayCancellation = new CancellationTokenSource();
        _ = SaveSettingsAfterDelayAsync(
            appSettings,
            settingsSaveDelayCancellation.Token);
    }

    private async Task SaveSettingsAfterDelayAsync(
        ApplicationSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SettingsSaveDelay, cancellationToken);
            await settingsStore.SaveAsync(settings, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer settings snapshot superseded this scheduled save.
        }
        catch (Exception exception)
        {
            RunOnUiThread(
                () => ShowUiError(
                    $"Settings could not be saved: {exception.Message}"));
        }
    }

    private void ShowUiError(string message)
    {
        ErrorTextBlock.Text = message;
        StatusTextBlock.Text = "The requested operation did not complete.";
        lastDiagnosticMessage = message;
        RefreshDiagnosticStatus();
        UpdateControlAvailability();
    }

    private void RunOnUiThread(Action action)
    {
        if (isClosing || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(action);
    }

    private async void MainWindow_Closing(
        object? sender,
        CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        if (shutdownStarted)
        {
            return;
        }

        shutdownStarted = true;
        isClosing = true;
        IsEnabled = false;
        settingsWindow.AllowRealClose();
        settingsWindow.Close();
        settingsSaveDelayCancellation?.Cancel();
        settingsSaveDelayCancellation?.Dispose();
        settingsSaveDelayCancellation = null;

        var shutdownErrors = new List<string>();

        try
        {
            await loudnessAnalysisService.DisposeAsync();
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "Loudness analysis could not be stopped cleanly: "
                + exception.Message);
        }

        try
        {
            if (hotkeyService is not null)
            {
                hotkeyService.HotkeyInvoked -=
                    HotkeyService_HotkeyInvoked;
                hotkeyService.Dispose();
                hotkeyService = null;
            }
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "Global hotkeys could not be fully unregistered: "
                + exception.Message);
        }

        try
        {
            UpdateSettingsFromUi();
            await settingsStore.SaveAsync(appSettings);
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "Settings could not be saved: " + exception.Message);
        }

        try
        {
            await soundTriggerGate.WaitAsync();
            soundTriggerGate.Release();
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "A sound trigger did not finish cleanly: "
                + exception.Message);
        }

        try
        {
            await libraryActionGate.WaitAsync();
            libraryActionGate.Release();
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "A library operation did not finish cleanly: "
                + exception.Message);
        }

        try
        {
            previewService.Dispose();
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "Preview audio could not be released cleanly: "
                + exception.Message);
        }

        try
        {
            await Task.Run(audioEngine.Dispose);
        }
        catch (Exception exception)
        {
            shutdownErrors.Add(
                "Main audio devices could not be released cleanly: "
                + exception.Message);
        }

        audioEngine.StateChanged -= AudioEngine_StateChanged;
        audioEngine.ErrorOccurred -= AudioEngine_ErrorOccurred;
        audioEngine.PeakLevelsChanged -= AudioEngine_PeakLevelsChanged;
        audioEngine.SoundPlaybackStateChanged -=
            AudioEngine_SoundPlaybackStateChanged;
        await soundLibraryStore.DisposeAsync();
        await settingsStore.DisposeAsync();
        libraryActionGate.Dispose();
        soundTriggerGate.Dispose();

        if (shutdownErrors.Count > 0)
        {
            MessageBox.Show(
                this,
                "Soundboard encountered an error while closing:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, shutdownErrors),
                "Soundboard shutdown",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        allowClose = true;
        Close();
    }

    private static AudioEndpoint? FindById(
        IEnumerable<AudioEndpoint> endpoints,
        string? deviceId)
    {
        return deviceId is null
            ? null
            : endpoints.FirstOrDefault(
                endpoint => string.Equals(
                    endpoint.DeviceId,
                    deviceId,
                    StringComparison.Ordinal));
    }

    private AudioEndpoint? GetSafePreviewEndpoint(
        out string availabilityMessage)
    {
        var selected =
            MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        if (selected is
            {
                IsLikelyVbCable: false,
                Direction: AudioDeviceDirection.Render
            }
            && selected.State.HasFlag(AudioEndpointState.Active))
        {
            availabilityMessage =
                $"Preview will use the selected physical monitor endpoint: "
                + $"{selected.FriendlyName}.";
            return selected;
        }

        if (audioEngine.State == AudioEngineState.Stopped)
        {
            var fallback = currentSnapshot?.RenderEndpoints
                .FirstOrDefault(
                    endpoint =>
                        !endpoint.IsLikelyVbCable
                        && endpoint.Direction
                            == AudioDeviceDirection.Render
                        && endpoint.State.HasFlag(
                            AudioEndpointState.Active)
                        && endpoint.IsDefault);
            if (fallback is not null)
            {
                availabilityMessage =
                    $"The selected monitor endpoint is unavailable. Preview "
                    + $"will use the current default safe physical endpoint "
                    + $"{fallback.FriendlyName} while the engine is stopped.";
                return fallback;
            }
        }

        availabilityMessage = audioEngine.State == AudioEngineState.Running
            ? "Preview is unavailable while the engine is running because "
                + "the already selected physical monitor endpoint is invalid "
                + "or unavailable. The engine will not be stopped or switched."
            : "No active non-virtual physical render endpoint is available "
                + "for local preview.";
        return null;
    }

    private static bool IsVisibleWindowPosition(
        double left,
        double top,
        double width,
        double height)
    {
        if (!double.IsFinite(left)
            || !double.IsFinite(top)
            || !double.IsFinite(width)
            || !double.IsFinite(height))
        {
            return false;
        }

        var window = new Rect(left, top, width, height);
        var desktop = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        window.Intersect(desktop);
        return window.Width >= 100d && window.Height >= 100d;
    }

    private static string FormatChannelCount(int channels)
    {
        return channels switch
        {
            1 => "mono",
            2 => "stereo",
            _ => $"{channels} channels"
        };
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static string YesNoOrNotActive(bool? value)
    {
        return value is { } active
            ? YesNo(active)
            : "N/A (no active monitor sound branch)";
    }

    private enum SoundTriggerSource
    {
        Mouse,
        Hotkey
    }

    /// <summary>
    /// The single next action offered by the current empty state.
    /// </summary>
    private enum EmptyStateAction
    {
        None,
        Import,
        ClearSearch,
        ShowAll
    }
}
