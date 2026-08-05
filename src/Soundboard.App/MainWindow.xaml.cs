using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    private static readonly TimeSpan DeviceChangeSettleDelay =
        TimeSpan.FromMilliseconds(750);

    private readonly AudioDeviceService audioDeviceService = new();
    private readonly AudioMixEngine audioEngine = new();
    private readonly AudioServiceLifecycle audioServiceLifecycle;
    private readonly SoundLibraryStore soundLibraryStore = new();
    private readonly WaveformCacheService waveformCacheService = new();
    private readonly AudioPreviewService previewService = new();
    private readonly ApplicationSettingsStore settingsStore = new();
    private readonly ObservableCollection<SoundTileViewModel> soundTiles = [];
    private readonly ObservableCollection<SoundCategory> soundCategories = [];
    private readonly ObservableCollection<LibraryViewItem> libraryViews = [];
    private readonly SemaphoreSlim libraryActionGate = new(1, 1);
    private readonly SemaphoreSlim soundTriggerGate = new(1, 1);
    private readonly SemaphoreSlim audioServiceGate = new(1, 1);
    private readonly ICollectionView soundTilesView;

    /// <summary>
    /// Organization-mode selection. Deliberately separate from the library
    /// store: it holds sound IDs only and is never persisted.
    /// </summary>
    private readonly LibrarySelectionState librarySelection = new();

    private readonly DispatcherTimer undoNotificationTimer = new()
    {
        Interval = TimeSpan.FromSeconds(10)
    };

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
    private string? pinnedMicrophoneEndpointId;
    private string? configuredVirtualOutputEndpointId;
    private CancellationTokenSource? settingsSaveDelayCancellation;
    private CancellationTokenSource? deviceChangeDebounceCancellation;
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
    private long audioServiceRequestGeneration;
    private long currentAudioEngineSessionId;
    private EmptyStateAction emptyStateAction = EmptyStateAction.Import;
    private CategoryEditorMode categoryEditorMode = CategoryEditorMode.Hidden;
    private Guid? categoryEditorCategoryId;
    private IReadOnlyList<Guid> pendingCategoryMoveSoundIds = [];
    private bool pendingCategoryImport;
    private SoundCategoryMoveUndo? lastMoveUndo;
    private Guid? sessionImportCategoryId;
    private bool sessionImportDestinationChosen;
    private LibraryViewItem? activeDropTarget;

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

    private Slider MonitorVolumeSlider =>
        settingsWindow.MonitorVolumeSlider;

    private CheckBox MonitorSoundsCheckBox =>
        settingsWindow.MonitorSoundsCheckBox;

    private CheckBox GlobalHotkeysCheckBox =>
        settingsWindow.GlobalHotkeysCheckBox;

    private ComboBox VoiceSensitivityComboBox =>
        settingsWindow.VoiceSensitivityComboBox;

    private ComboBox VoiceDuckingStrengthComboBox =>
        settingsWindow.VoiceDuckingStrengthComboBox;

    private TextBlock VoicePriorityStateTextBlock =>
        settingsWindow.VoicePriorityStateTextBlock;

    private CheckBox UseDefaultMicrophoneCheckBox =>
        settingsWindow.UseDefaultMicrophoneCheckBox;

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

    private Button ClearPauseHotkeyButton =>
        settingsWindow.ClearPauseHotkeyButton;

    private TextBlock PauseHotkeyDisplayTextBlock =>
        settingsWindow.PauseHotkeyDisplayTextBlock;

    private TextBlock PauseHotkeyStateTextBlock =>
        settingsWindow.PauseHotkeyStateTextBlock;

    private TextBox DiagnosticStatusTextBox =>
        settingsWindow.DiagnosticStatusTextBox;

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
        audioServiceLifecycle = new AudioServiceLifecycle(audioEngine);
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
        UseDefaultMicrophoneCheckBox.Checked +=
            UseDefaultMicrophoneCheckBox_Changed;
        UseDefaultMicrophoneCheckBox.Unchecked +=
            UseDefaultMicrophoneCheckBox_Changed;
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
        VoicePriorityCheckBox.Checked += VoicePriorityCheckBox_Changed;
        VoicePriorityCheckBox.Unchecked += VoicePriorityCheckBox_Changed;
        VoiceSensitivityComboBox.SelectionChanged +=
            VoicePriorityComboBox_SelectionChanged;
        VoiceDuckingStrengthComboBox.SelectionChanged +=
            VoicePriorityComboBox_SelectionChanged;

        // Settings-window commands are wired here rather than in that
        // window's XAML so all application logic stays in one place.
        RefreshDevicesButton.Click += RefreshDevicesButton_Click;
        settingsWindow.TestMicrophoneButton.Click += TestMicrophoneButton_Click;
        settingsWindow.CompleteSetupButton.Click += CompleteSetupButton_Click;
        RetryHotkeysButton.Click += RetryHotkeysButton_Click;
        settingsWindow.AssignStopHotkeyButton.Click +=
            AssignStopHotkeyButton_Click;
        ClearStopHotkeyButton.Click += ClearStopHotkeyButton_Click;
        settingsWindow.AssignPauseHotkeyButton.Click +=
            AssignPauseHotkeyButton_Click;
        ClearPauseHotkeyButton.Click += ClearPauseHotkeyButton_Click;
        settingsWindow.StoragePathsTextBox.Text = string.Join(
            Environment.NewLine,
            $"Library:        {soundLibraryStore.RootPath}",
            $"Waveform cache: {waveformCacheService.WaveformsPath}");

        SizeChanged += MainWindow_SizeChanged;
        undoNotificationTimer.Tick += UndoNotificationTimer_Tick;

        audioEngine.StateChanged += AudioEngine_StateChanged;
        audioEngine.ErrorOccurred += AudioEngine_ErrorOccurred;
        audioEngine.PeakLevelsChanged += AudioEngine_PeakLevelsChanged;
        audioEngine.SoundPlaybackStateChanged +=
            AudioEngine_SoundPlaybackStateChanged;
        audioDeviceService.DevicesChanged += AudioDeviceService_DevicesChanged;
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        UpdateSelectionPresentation();
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
            await audioServiceGate.WaitAsync();
            try
            {
                await StartAudioServiceAsync("application startup");
            }
            finally
            {
                audioServiceGate.Release();
            }
            if (!appSettings.SetupCompleted)
            {
                settingsWindow.SetupBanner.Visibility = Visibility.Visible;
                settingsWindow.ShowOrActivate(this);
            }
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
        pinnedMicrophoneEndpointId = appSettings.MicrophoneEndpointId;
        configuredVirtualOutputEndpointId =
            appSettings.VirtualOutputEndpointId;
        ApplySettingsToWindowAndControls();

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

        if (appSettings.PauseResumeHotkey is not null)
        {
            hotkeyService.LoadPersistedBinding(
                HotkeyTarget.PauseResumePlayback,
                appSettings.PauseResumeHotkey);
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
            UseDefaultMicrophoneCheckBox.IsChecked =
                appSettings.UseDefaultMicrophone;
            MicrophoneComboBox.IsEnabled =
                !appSettings.UseDefaultMicrophone;
            SoundVolumeSlider.Value =
                appSettings.SoundVolume * 100d;
            MonitorSoundsCheckBox.IsChecked =
                appSettings.MonitoringEnabled;
            MonitorVolumeSlider.Value =
                appSettings.MonitorVolume * 100d;
            GlobalHotkeysCheckBox.IsChecked =
                appSettings.GlobalHotkeysEnabled;
            VoicePriorityCheckBox.IsChecked =
                appSettings.VoicePriorityEnabled;
            VoiceSensitivityComboBox.SelectedIndex =
                (int)appSettings.VoicePrioritySensitivity;
            VoiceDuckingStrengthComboBox.SelectedIndex =
                (int)appSettings.VoicePriorityStrength;
            ApplyVoicePriorityToEngine();
            audioEngine.SoundVolume = AudioGain.FromPercent(
                appSettings.SoundVolume * 100d);
            audioEngine.MonitorVolume = AudioGain.FromPercent(
                appSettings.MonitorVolume * 100d);
            settingsWindow.SetupBanner.Visibility = appSettings.SetupCompleted
                ? Visibility.Collapsed
                : Visibility.Visible;

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

    /// <summary>
    /// The overflow button opens the tile's own context menu, so the pointer
    /// shortcut and a right-click or the Menu key always show exactly the
    /// same commands.
    /// </summary>
    private void SoundOverflowButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var element = sender as FrameworkElement;
        while (element is not null && element.ContextMenu is null)
        {
            element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        }

        OpenAttachedContextMenu(element!);
    }

    private static void OpenAttachedContextMenu(object? sender)
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
            await RestartAudioServiceAsync("manual troubleshooting restart");
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
        string? preferredMonitorId = null,
        AudioDeviceSnapshot? knownSnapshot = null,
        bool preserveOperationalStatus = false)
    {
        var previousStatus = StatusTextBlock.Text;
        var previousError = ErrorTextBlock.Text;
        var previousDiagnostic = lastDiagnosticMessage;
        var refreshedWithoutWarnings = false;
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
            var snapshot = knownSnapshot
                ?? await Task.Run(audioDeviceService.GetActiveDevices);
            currentSnapshot = snapshot;
            refreshedWithoutWarnings = snapshot.Warnings.Count == 0;

            var physicalCaptureEndpoints =
                AudioEndpointSelectionPolicy.PhysicalMicrophones(
                    snapshot.CaptureEndpoints);
            var virtualRenderEndpoints =
                AudioEndpointSelectionPolicy.VirtualOutputs(
                    snapshot.RenderEndpoints);
            MicrophoneComboBox.ItemsSource = physicalCaptureEndpoints;
            VirtualOutputComboBox.ItemsSource = virtualRenderEndpoints;
            var physicalRenderEndpoints = snapshot.RenderEndpoints
                .Where(endpoint => !endpoint.IsLikelyVbCable)
                .ToArray();
            MonitorOutputComboBox.ItemsSource = physicalRenderEndpoints;

            MicrophoneComboBox.SelectedItem =
                AudioEndpointSelectionPolicy.SelectMicrophone(
                    physicalCaptureEndpoints,
                    appSettings.UseDefaultMicrophone,
                    selectedCaptureId);
            var selectedMicrophone =
                MicrophoneComboBox.SelectedItem as AudioEndpoint;
            var pinnedMicrophoneUnavailable =
                !appSettings.UseDefaultMicrophone
                && !string.IsNullOrWhiteSpace(selectedCaptureId)
                && !string.Equals(
                    selectedMicrophone?.DeviceId,
                    selectedCaptureId,
                    StringComparison.Ordinal);
            settingsWindow.MicrophoneSelectionStatusTextBlock.Text =
                selectedMicrophone is null
                    ? "No usable physical microphone is available. Soundboard will recover automatically when one appears."
                    : pinnedMicrophoneUnavailable
                        ? $"The pinned microphone is unavailable. Temporarily using {selectedMicrophone.FriendlyName}; the pinned device will be restored when it returns."
                        : appSettings.UseDefaultMicrophone
                            ? $"Following the Windows default communications microphone: {selectedMicrophone.FriendlyName}."
                            : $"Pinned to {selectedMicrophone.FriendlyName}.";

            var savedVirtualOutput =
                AudioEndpointSelectionPolicy.SelectVirtualOutput(
                virtualRenderEndpoints,
                selectedRenderId);
            var configuredVirtualOutputUnavailable =
                !string.IsNullOrWhiteSpace(selectedRenderId)
                && savedVirtualOutput is null;
            VirtualOutputComboBox.SelectedItem = savedVirtualOutput
                ?? (configuredVirtualOutputUnavailable
                    ? null
                    : AudioEndpointSelectionPolicy.SelectVirtualOutput(
                        virtualRenderEndpoints,
                        configuredEndpointId: null));
            if (configuredVirtualOutputEndpointId is null
                && VirtualOutputComboBox.SelectedItem
                    is AudioEndpoint discoveredVirtualOutput)
            {
                configuredVirtualOutputEndpointId =
                    discoveredVirtualOutput.DeviceId;
            }

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
            if (configuredVirtualOutputUnavailable)
            {
                var warning =
                    "The configured VB-CABLE render endpoint is unavailable. "
                    + "Its saved endpoint ID was preserved; reconnect or "
                    + "re-enable that endpoint, or explicitly select a "
                    + "replacement in Settings.";
                ErrorTextBlock.Text = warning;
                StatusTextBlock.Text =
                    "Waiting for the configured Soundboard microphone endpoint.";
                lastDiagnosticMessage = warning;
            }
            else if (savedMonitorRejected)
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
            if (preserveOperationalStatus
                && refreshedWithoutWarnings)
            {
                StatusTextBlock.Text = previousStatus;
                ErrorTextBlock.Text = previousError;
                lastDiagnosticMessage = previousDiagnostic;
            }
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
    }

    private async void DeviceComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        var programmaticSelection = isApplyingSettings || isRefreshing;
        try
        {
            await UpdateSelectedFormatsAsync();
            UpdateRoutingStatusForSelection();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
            if (!programmaticSelection)
            {
                if (ReferenceEquals(sender, MicrophoneComboBox)
                    && UseDefaultMicrophoneCheckBox.IsChecked != true
                    && MicrophoneComboBox.SelectedItem
                        is AudioEndpoint selectedMicrophone)
                {
                    pinnedMicrophoneEndpointId =
                        AudioEndpointSelectionPolicy
                            .UpdateConfiguredEndpointId(
                                pinnedMicrophoneEndpointId,
                                selectedMicrophone,
                                userInitiated: true);
                }

                if (ReferenceEquals(sender, VirtualOutputComboBox))
                {
                    configuredVirtualOutputEndpointId =
                        AudioEndpointSelectionPolicy
                            .UpdateConfiguredEndpointId(
                                configuredVirtualOutputEndpointId,
                                VirtualOutputComboBox.SelectedItem
                                    as AudioEndpoint,
                                userInitiated: true);
                }

                UpdateSettingsFromUi();
                ScheduleSettingsSave();
                await RestartAudioServiceAsync("device selection changed");
            }
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

    private async Task StartAudioServiceAsync(string reason)
    {
        var microphone = MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render = VirtualOutputComboBox.SelectedItem as AudioEndpoint;
        if (microphone is null || render is null)
        {
            StatusTextBlock.Text = microphone is null
                ? "No usable physical microphone is available. Soundboard will connect automatically when one appears."
                : "The Soundboard microphone is unavailable. Install or enable VB-CABLE, then Soundboard will reconnect automatically.";
            lastDiagnosticMessage = $"Audio service waiting after {reason}.";
            UpdateEnginePresentation();
            return;
        }

        var monitor = MonitorOutputComboBox.SelectedItem as AudioEndpoint;
        try
        {
            StatusTextBlock.Text = "Connecting microphone…";
            await audioServiceLifecycle.ConnectAsync(
                microphone.DeviceId,
                render.DeviceId,
                new AudioMonitorConfiguration(
                    MonitorSoundsCheckBox.IsChecked == true,
                    monitor?.DeviceId));
            StatusTextBlock.Text =
                $"Ready. {microphone.FriendlyName} is continuously mixed into the Soundboard microphone.";
            lastDiagnosticMessage = $"Audio service started automatically after {reason}.";
        }
        catch (Exception exception)
        {
            lastDiagnosticMessage = exception.Message;
            ErrorTextBlock.Text =
                "Audio is temporarily unavailable; Soundboard will retry after the next device change. "
                + exception.Message;
        }
        finally
        {
            UpdateEnginePresentation();
            UpdateControlAvailability();
            RefreshDiagnosticStatus();
        }
    }

    private async Task RestartAudioServiceAsync(
        string reason,
        AudioDeviceSnapshot? knownSnapshot = null,
        bool keepHealthyRoute = false,
        CancellationToken cancellationToken = default)
    {
        var requestGeneration = Interlocked.Increment(
            ref audioServiceRequestGeneration);
        if (isClosing)
        {
            return;
        }

        await audioServiceGate.WaitAsync(cancellationToken);
        try
        {
            if (isClosing
                || requestGeneration
                    != Interlocked.Read(ref audioServiceRequestGeneration))
            {
                return;
            }

            if (keepHealthyRoute
                && knownSnapshot is not null
                && IsCurrentAudioRouteHealthy(knownSnapshot))
            {
                // Endpoint notifications can describe unrelated or transient
                // changes. Refresh the selectors without interrupting a route
                // whose selected endpoints and streams are still healthy.
                await RefreshDevicesAsync(
                    appSettings.MicrophoneEndpointId,
                    appSettings.VirtualOutputEndpointId,
                    appSettings.MonitorOutputEndpointId,
                    knownSnapshot,
                    preserveOperationalStatus: true);
                return;
            }

            await audioServiceLifecycle.StopAsync();

            await RefreshDevicesAsync(
                appSettings.MicrophoneEndpointId,
                appSettings.VirtualOutputEndpointId,
                appSettings.MonitorOutputEndpointId,
                knownSnapshot);
            if (isClosing
                || requestGeneration
                    != Interlocked.Read(ref audioServiceRequestGeneration))
            {
                return;
            }

            await StartAudioServiceAsync(reason);
        }
        finally
        {
            audioServiceGate.Release();
        }
    }

    private void AudioDeviceService_DevicesChanged(
        object? sender,
        AudioDeviceChangedEventArgs eventArgs)
    {
        if (isClosing
            || eventArgs.Kind == AudioDeviceChangeKind.PropertyChanged)
        {
            // Core Audio property notifications include harmless endpoint
            // metadata churn. WASAPI stop callbacks remain authoritative for
            // a real capture or render stream failure.
            return;
        }

        RunOnUiThread(() =>
        {
            deviceChangeDebounceCancellation?.Cancel();
            deviceChangeDebounceCancellation?.Dispose();
            deviceChangeDebounceCancellation = new CancellationTokenSource();
            _ = ReconcileAfterDeviceChangeAsync(
                eventArgs,
                deviceChangeDebounceCancellation.Token);
        });
    }

    private void SystemEvents_PowerModeChanged(
        object sender,
        PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode != PowerModes.Resume || isClosing)
        {
            return;
        }

        RunOnUiThread(
            () => _ = RestartAudioServiceAsync("Windows resumed from sleep"));
    }

    private async Task ReconcileAfterDeviceChangeAsync(
        AudioDeviceChangedEventArgs eventArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(DeviceChangeSettleDelay, cancellationToken);
            var snapshot = await Task.Run(
                audioDeviceService.GetActiveDevices,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RestartAudioServiceAsync(
                $"Windows audio device {eventArgs.Kind.ToString().ToLowerInvariant()}",
                snapshot,
                keepHealthyRoute: true,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A newer endpoint event superseded this reconciliation.
        }
        catch (Exception exception)
        {
            if (!isClosing)
            {
                ShowUiError(
                    "Windows audio devices could not be reconciled: "
                    + exception.Message);
            }
        }
    }

    private bool IsCurrentAudioRouteHealthy(AudioDeviceSnapshot snapshot)
    {
        var diagnostics = audioEngine.Diagnostics;
        if (audioEngine.State != AudioEngineState.Running
            || diagnostics is null)
        {
            return false;
        }

        var physicalMicrophones =
            AudioEndpointSelectionPolicy.PhysicalMicrophones(
                snapshot.CaptureEndpoints);
        var microphone = AudioEndpointSelectionPolicy.SelectMicrophone(
            physicalMicrophones,
            appSettings.UseDefaultMicrophone,
            appSettings.MicrophoneEndpointId);
        var virtualOutput = AudioEndpointSelectionPolicy.SelectVirtualOutput(
            AudioEndpointSelectionPolicy.VirtualOutputs(
                snapshot.RenderEndpoints),
            appSettings.VirtualOutputEndpointId);
        var physicalRenderEndpoints = snapshot.RenderEndpoints
            .Where(endpoint => !endpoint.IsLikelyVbCable)
            .ToArray();
        var savedMonitor = FindById(
            snapshot.RenderEndpoints,
            appSettings.MonitorOutputEndpointId);
        var monitor =
            (savedMonitor is { IsLikelyVbCable: false }
                ? FindById(physicalRenderEndpoints, savedMonitor.DeviceId)
                : null)
            ?? physicalRenderEndpoints.FirstOrDefault(
                endpoint => endpoint.IsDefault)
            ?? physicalRenderEndpoints.FirstOrDefault();
        var relatedVirtualCaptureIsActive = snapshot.CaptureEndpoints.Any(
            endpoint => string.Equals(
                endpoint.DeviceId,
                diagnostics.RelatedVbCableCaptureEndpointId,
                StringComparison.Ordinal));

        return microphone is not null
            && virtualOutput is not null
            && relatedVirtualCaptureIsActive
            && string.Equals(
                microphone.DeviceId,
                diagnostics.MicrophoneEndpointId,
                StringComparison.Ordinal)
            && string.Equals(
                virtualOutput.DeviceId,
                diagnostics.RenderEndpointId,
                StringComparison.Ordinal)
            && diagnostics.MonitoringEnabled == appSettings.MonitoringEnabled
            && (!appSettings.MonitoringEnabled
                || string.Equals(
                    monitor?.DeviceId,
                    diagnostics.MonitorEndpointId,
                    StringComparison.Ordinal));
    }

    private async void UseDefaultMicrophoneCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (isApplyingSettings)
        {
            return;
        }

        MicrophoneComboBox.IsEnabled =
            UseDefaultMicrophoneCheckBox.IsChecked != true;
        if (UseDefaultMicrophoneCheckBox.IsChecked != true
            && MicrophoneComboBox.SelectedItem
                is AudioEndpoint selectedMicrophone)
        {
            pinnedMicrophoneEndpointId = AudioEndpointSelectionPolicy
                .UpdateConfiguredEndpointId(
                    pinnedMicrophoneEndpointId,
                    selectedMicrophone,
                    userInitiated: true);
        }

        UpdateSettingsFromUi();
        ScheduleSettingsSave();
        await RestartAudioServiceAsync("microphone mode changed");
    }

    private void TestMicrophoneButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        settingsWindow.SettingsTabControl.SelectedIndex = 0;
        var message = audioEngine.State == AudioEngineState.Running
            ? "Speak now: the microphone meter should move. Voice is already being sent to the Soundboard microphone."
            : "No microphone is connected yet. Soundboard will reconnect automatically when one becomes available.";
        StatusTextBlock.Text = message;
        settingsWindow.MicrophoneTestStatusTextBlock.Text = message;
    }

    private async void CompleteSetupButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (MicrophoneComboBox.SelectedItem is not AudioEndpoint
            || VirtualOutputComboBox.SelectedItem is not AudioEndpoint)
        {
            ShowUiError(
                "Connect a microphone and install/enable VB-CABLE before completing setup.");
            return;
        }

        appSettings = appSettings with { SetupCompleted = true };
        settingsWindow.SetupBanner.Visibility = Visibility.Collapsed;
        UpdateSettingsFromUi();
        await settingsStore.SaveAsync(appSettings);
        await RestartAudioServiceAsync("setup completed");
        settingsWindow.Hide();
    }

    private void SoundVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        SoundVolumeTextBlock.Text = $"{percent}%";
        audioEngine.SoundVolume = AudioGain.FromPercent(percent);
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
        if (!isApplyingSettings)
        {
            _ = RestartAudioServiceAsync("monitoring setting changed");
        }
    }

    private void MonitorVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        MonitorVolumeTextBlock.Text = $"{percent}%";
        audioEngine.MonitorVolume = AudioGain.FromPercent(percent);
        UpdateSettingsFromUi();
        ScheduleSettingsSave();
    }

    private void VoicePriorityCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (isApplyingSettings)
        {
            return;
        }

        UpdateSettingsFromUi();
        ApplyVoicePriorityToEngine();
        ScheduleSettingsSave();
        StatusTextBlock.Text = appSettings.VoicePriorityEnabled
            ? "Voice Priority is on. Sounds are lowered automatically while "
                + "you speak."
            : "Voice Priority is off. Sounds keep their configured volume.";
        RefreshDiagnosticStatus();
    }

    private void VoicePriorityComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (isApplyingSettings)
        {
            return;
        }

        UpdateSettingsFromUi();
        ApplyVoicePriorityToEngine();
        ScheduleSettingsSave();
        RefreshDiagnosticStatus();
    }

    /// <summary>
    /// Pushes the current Voice Priority choice into the running engine. The
    /// engine keeps the setting across device reconnects, so no audio restart
    /// is needed and playback is never interrupted.
    /// </summary>
    private void ApplyVoicePriorityToEngine()
    {
        var settings = CurrentVoicePrioritySettings;
        audioEngine.VoicePriority = settings;
        VoicePriorityStateTextBlock.Text = settings.Enabled
            ? $"Voice Priority is on. Sounds drop by "
                + $"{-settings.DuckingDb:0} dB while speech is above "
                + $"{settings.EngageThresholdDb:0} dBFS."
            : "Voice Priority is off.";
        if (!settings.Enabled)
        {
            VoicePriorityStatusTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private VoicePrioritySettings CurrentVoicePrioritySettings =>
        new(
            VoicePriorityCheckBox.IsChecked == true,
            (VoiceSensitivity)Math.Clamp(
                VoiceSensitivityComboBox.SelectedIndex,
                0,
                2),
            (VoiceDuckingStrength)Math.Clamp(
                VoiceDuckingStrengthComboBox.SelectedIndex,
                0,
                2));

    /// <summary>
    /// Import always names a destination before any file is inspected. A
    /// selected user category is an unambiguous destination and is used
    /// directly; the built-in views are not, so the destination is chosen
    /// first from a short menu.
    /// </summary>
    private void ImportSoundsButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SelectedLibraryView is
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } selectedCategoryId
            })
        {
            RememberImportDestination(selectedCategoryId);
            ShowImportFileDialog(selectedCategoryId);
            return;
        }

        ShowImportDestinationMenu(
            sender as FrameworkElement ?? ImportSoundsButton);
    }

    private void ShowImportDestinationMenu(FrameworkElement placementTarget)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom
        };
        menu.Items.Add(
            new MenuItem
            {
                Header = "Import into…",
                IsEnabled = false
            });
        menu.Items.Add(new Separator());
        foreach (var item in BuildCategoryChoiceItems(
            sessionImportCategoryId,
            sessionImportDestinationChosen,
            " (last used)",
            disableMarked: false,
            categoryId =>
            {
                RememberImportDestination(categoryId);
                ShowImportFileDialog(categoryId);
            },
            BeginCategoryEditorForImport))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ShowImportFileDialog(Guid? categoryId)
    {
        var dialog = new OpenFileDialog
        {
            Title = categoryId is null
                ? "Import sounds"
                : $"Import sounds into {GetCategoryName(categoryId)}",
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
            _ = ImportPathsFromUiAsync(dialog.FileNames, categoryId);
        }
    }

    /// <summary>
    /// Keeps the last explicitly chosen destination for this session so a
    /// long import session does not ask the same question repeatedly. It is
    /// never written to settings.
    /// </summary>
    private void RememberImportDestination(Guid? categoryId)
    {
        sessionImportCategoryId = categoryId;
        sessionImportDestinationChosen = true;
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

        var canImport = eventArgs.Data.GetDataPresent(DataFormats.FileDrop)
            && !isImporting;
        eventArgs.Effects = canImport
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        if (canImport)
        {
            SoundboardDropArea.BorderBrush = ThemeBrush("AccentBrush");
            StatusTextBlock.Text =
                "Drop to import into "
                + $"{DescribeImportDestination(ActiveImportCategoryId)}.";
        }

        eventArgs.Handled = true;
    }

    private void SoundboardDropArea_DragLeave(
        object sender,
        DragEventArgs eventArgs)
    {
        SoundboardDropArea.BorderBrush = Brushes.Transparent;
    }

    private void SoundboardDropArea_Drop(
        object sender,
        DragEventArgs eventArgs)
    {
        SoundboardDropArea.BorderBrush = Brushes.Transparent;
        if (eventArgs.Data.GetData(DataFormats.FileDrop)
            is string[] paths)
        {
            _ = ImportPathsFromUiAsync(paths, ActiveImportCategoryId);
        }
    }

    /// <summary>
    /// Destination used when files are dropped straight onto the grid: the
    /// selected user category, otherwise Uncategorized.
    /// </summary>
    private Guid? ActiveImportCategoryId =>
        SelectedLibraryView is
        {
            Kind: SoundLibraryViewKind.Category,
            CategoryId: { } categoryId
        }
            ? categoryId
            : null;

    private string DescribeImportDestination(Guid? categoryId) =>
        categoryId is null
            ? "Uncategorized"
            : GetCategoryName(categoryId);

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
        var destinationName = DescribeImportDestination(categoryId);
        StatusTextBlock.Text =
            $"Inspecting and importing {sourcePaths.Count} file(s) into "
            + $"{destinationName}…";

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
            StatusTextBlock.Text = BuildImportSummary(result, destinationName);
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

    /// <summary>
    /// Leads with the outcome that matters — how many sounds landed where —
    /// and only mentions the skipped counts when there are any.
    /// </summary>
    private static string BuildImportSummary(
        SoundImportResult result,
        string destinationName)
    {
        var summary = result.Imported.Count switch
        {
            0 => $"No sounds were imported into {destinationName}.",
            1 => $"1 sound imported into {destinationName}.",
            _ => $"{result.Imported.Count} sounds imported into "
                + $"{destinationName}."
        };

        var skipped = new List<string>();
        if (result.Duplicates.Count > 0)
        {
            skipped.Add($"{result.Duplicates.Count} duplicate(s)");
        }

        if (result.InvalidFiles.Count > 0)
        {
            skipped.Add($"{result.InvalidFiles.Count} unsupported file(s)");
        }

        if (result.Errors.Count > 0)
        {
            skipped.Add($"{result.Errors.Count} error(s)");
        }

        return skipped.Count == 0
            ? summary
            : $"{summary} Skipped {string.Join(", ", skipped)}.";
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

        // Ctrl+click is the standard way into a list selection, so it enters
        // organization mode instead of playing.
        var extend = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var range = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (librarySelection.IsActive || extend || range)
        {
            librarySelection.ApplyClick(
                VisibleSoundIds(),
                tile.Id,
                extend,
                range);
            OrganizeToggleButton.IsChecked = true;
            UpdateSelectionPresentation();
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

    private async void PausePlaybackButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await TogglePlaybackPauseAsync(SoundTriggerSource.Mouse);
    }

    /// <summary>
    /// Flips the single global paused state. Only decoded sound sessions are
    /// held; microphone passthrough, the audio service, and the device
    /// connections keep running.
    /// </summary>
    private async Task TogglePlaybackPauseAsync(SoundTriggerSource source)
    {
        await soundTriggerGate.WaitAsync();
        try
        {
            if (isClosing)
            {
                return;
            }

            lastSoundTriggerSource = source;
            if (!audioEngine.CanPausePlayback)
            {
                if (source == SoundTriggerSource.Hotkey)
                {
                    lastHotkeyAction = "Pause/Resume playback (no sound)";
                }

                StatusTextBlock.Text =
                    "There is no sound to pause. The microphone remains "
                    + "active.";
                return;
            }

            var paused = await Task.Run(audioEngine.TogglePlaybackPause);
            if (source == SoundTriggerSource.Hotkey)
            {
                lastHotkeyAction = paused
                    ? "Pause playback"
                    : "Resume playback";
            }

            StatusTextBlock.Text = paused
                ? "Sounds are paused at their current position. The "
                    + "microphone keeps running."
                : "Sounds resumed from their paused position.";
        }
        catch (Exception exception)
        {
            var message =
                $"Playback could not be paused or resumed: {exception.Message}";
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
                await RestartAudioServiceAsync("a sound was triggered while audio was unavailable");
                engineState = audioEngine.State;
            }

            if (engineState != AudioEngineState.Running)
            {
                var message = engineState switch
                {
                    AudioEngineState.Stopped =>
                        "Sound request is waiting for a usable microphone and Soundboard microphone.",
                    AudioEngineState.Starting =>
                        "Sound request could not play while audio was connecting.",
                    AudioEngineState.Stopping =>
                        "Sound request could not play while audio was reconnecting.",
                    AudioEngineState.Faulted =>
                        "Sound request could not play because audio is temporarily unavailable.",
                    _ =>
                        $"Sound request could not play while audio is "
                        + $"{engineState}."
                };
                StatusTextBlock.Text = message;
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

                ErrorTextBlock.Text = string.Empty;

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
                        tile.Sound.VolumePercent));

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

            switch (eventArgs.Target.Kind)
            {
                case HotkeyTargetKind.StopSound:
                    await StopCurrentSoundAsync(SoundTriggerSource.Hotkey);
                    break;
                case HotkeyTargetKind.PauseResumePlayback:
                    await TogglePlaybackPauseAsync(SoundTriggerSource.Hotkey);
                    break;
                default:
                    await TriggerSoundAsync(
                        eventArgs.Target.SoundId,
                        SoundTriggerSource.Hotkey);
                    break;
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

    private async void AssignPauseHotkeyButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new HotkeyAssignmentDialog(
            "Application action: Pause/Resume playback",
            appSettings.PauseResumeHotkey)
        {
            Owner = this
        };
        if (!ShowHotkeyAssignmentDialog(dialog))
        {
            return;
        }

        await ApplyPauseHotkeyAsync(
            dialog.ClearRequested
                ? null
                : dialog.ProposedHotkey);
    }

    private async void ClearPauseHotkeyButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await ApplyPauseHotkeyAsync(null);
    }

    private async Task ApplyPauseHotkeyAsync(HotkeyGesture? proposed)
    {
        if (hotkeyService is null)
        {
            ShowUiError("The global-hotkey service is not initialized.");
            return;
        }

        var conflict = FindLocalHotkeyConflict(
            HotkeyTarget.PauseResumePlayback,
            proposed);
        if (conflict is not null)
        {
            ShowUiError(conflict);
            return;
        }

        var previous = appSettings.PauseResumeHotkey;
        if (!hotkeyService.TryReplaceBinding(
                HotkeyTarget.PauseResumePlayback,
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
                PauseResumeHotkey = proposed
            };
            settingsSaveDelayCancellation?.Cancel();
            settingsSaveDelayCancellation?.Dispose();
            settingsSaveDelayCancellation = null;
            await settingsStore.SaveAsync(updatedSettings);
            appSettings = updatedSettings;
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = proposed is null
                ? "Cleared the Pause/Resume playback hotkey."
                : $"Assigned {proposed.DisplayText} to Pause/Resume playback "
                    + "and registered it with Windows.";
            lastHotkeyAction = proposed is null
                ? "Cleared Pause/Resume playback hotkey"
                : $"Assigned {proposed.DisplayText} to Pause/Resume playback";
        }
        catch (Exception exception)
        {
            hotkeyService.TryReplaceBinding(
                HotkeyTarget.PauseResumePlayback,
                previous,
                out var rollbackError);
            if (rollbackError is not null)
            {
                lastHotkeyRegistrationError = rollbackError;
            }

            ShowUiError(
                "The Pause/Resume playback hotkey could not be saved: "
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

        if (target != HotkeyTarget.PauseResumePlayback
            && appSettings.PauseResumeHotkey == proposed)
        {
            return $"{proposed.DisplayText} is already assigned to "
                + "Pause/Resume playback.";
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
                    dialog.TileAccent,
                    dialog.VolumePercent));
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
                    tile.Sound.TileAccent,
                    tile.Sound.VolumePercent));
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
            previewEndpoint,
            previewAvailabilityMessage,
            appSettings.SoundVolume * 100d)
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
            librarySelection.Retain(soundTiles.Select(item => item.Id));
            soundTilesView.Refresh();
            UpdateSelectionPresentation();
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

    // ---- Inline category editor ------------------------------------------

    private void CreateCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (categoryEditorMode == CategoryEditorMode.Create)
        {
            CloseCategoryEditor();
            return;
        }

        BeginCreateCategory([], pendingImport: false);
    }

    /// <summary>
    /// Opens the inline name field. The optional pending work runs as soon
    /// as the category exists, so "Create category…" inside a move menu or
    /// the import destination menu finishes what the user started.
    /// </summary>
    private void BeginCreateCategory(
        IReadOnlyList<Guid> pendingSoundIds,
        bool pendingImport)
    {
        categoryEditorMode = CategoryEditorMode.Create;
        categoryEditorCategoryId = null;
        pendingCategoryMoveSoundIds = pendingSoundIds;
        pendingCategoryImport = pendingImport;
        CategoryEditorTitleTextBlock.Text = pendingSoundIds.Count > 0
            ? "NEW CATEGORY FOR THE SELECTED SOUNDS"
            : "NEW CATEGORY";
        CategoryEditorConfirmButton.Content = "Create";
        CategoryEditorTextBox.Text = string.Empty;
        ShowCategoryEditor();
    }

    private void BeginCategoryEditorForImport() =>
        BeginCreateCategory([], pendingImport: true);

    private void BeginRenameCategory(Guid categoryId, string currentName)
    {
        categoryEditorMode = CategoryEditorMode.Rename;
        categoryEditorCategoryId = categoryId;
        pendingCategoryMoveSoundIds = [];
        pendingCategoryImport = false;
        CategoryEditorTitleTextBlock.Text = "RENAME CATEGORY";
        CategoryEditorConfirmButton.Content = "Rename";
        CategoryEditorTextBox.Text = currentName;
        ShowCategoryEditor();
    }

    private void ShowCategoryEditor()
    {
        CategoryEditorErrorTextBlock.Text = string.Empty;
        CategoryEditorPanel.Visibility = Visibility.Visible;
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                CategoryEditorTextBox.Focus();
                CategoryEditorTextBox.SelectAll();
            },
            DispatcherPriority.Input);
    }

    private void CloseCategoryEditor()
    {
        categoryEditorMode = CategoryEditorMode.Hidden;
        categoryEditorCategoryId = null;
        pendingCategoryMoveSoundIds = [];
        pendingCategoryImport = false;
        CategoryEditorPanel.Visibility = Visibility.Collapsed;
        CategoryEditorErrorTextBlock.Text = string.Empty;
    }

    private void CategoryEditorTextBox_TextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        CategoryEditorErrorTextBlock.Text = string.Empty;
    }

    private async void CategoryEditorTextBox_KeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Enter)
        {
            eventArgs.Handled = true;
            await CommitCategoryEditorAsync();
        }
        else if (eventArgs.Key == Key.Escape)
        {
            eventArgs.Handled = true;
            CloseCategoryEditor();
            LibraryViewsListBox.Focus();
        }
    }

    private async void CategoryEditorConfirmButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await CommitCategoryEditorAsync();
    }

    private void CategoryEditorCancelButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        CloseCategoryEditor();
        LibraryViewsListBox.Focus();
    }

    private async Task CommitCategoryEditorAsync()
    {
        var categoryName = CategoryEditorTextBox.Text.Trim();
        if (categoryName.Length == 0)
        {
            CategoryEditorErrorTextBlock.Text =
                "Enter a category name.";
            return;
        }

        switch (categoryEditorMode)
        {
            case CategoryEditorMode.Create:
                await CreateCategoryFromEditorAsync(categoryName);
                break;
            case CategoryEditorMode.Rename
                when categoryEditorCategoryId is { } categoryId:
                await RenameCategoryFromEditorAsync(
                    categoryId,
                    categoryName);
                break;
        }
    }

    private async Task CreateCategoryFromEditorAsync(string categoryName)
    {
        var soundIdsToMove = pendingCategoryMoveSoundIds;
        var importAfterCreate = pendingCategoryImport;

        if (!await libraryActionGate.WaitAsync(0))
        {
            CategoryEditorErrorTextBlock.Text =
                "Another library operation is already in progress.";
            return;
        }

        SoundCategory category;
        try
        {
            category = await soundLibraryStore.CreateCategoryAsync(
                categoryName);
            soundCategories.Add(category);
            RebuildLibraryViews(
                SoundLibraryViewKind.Category,
                category.Id);
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                $"Created category \"{category.DisplayName}\".";
            UpdateLibraryPresentation();
        }
        catch (Exception exception)
        {
            CategoryEditorErrorTextBlock.Text = exception.Message;
            return;
        }
        finally
        {
            libraryActionGate.Release();
        }

        CloseCategoryEditor();
        if (soundIdsToMove.Count > 0)
        {
            await MoveSoundsToCategoryAsync(soundIdsToMove, category.Id);
            return;
        }

        if (importAfterCreate)
        {
            RememberImportDestination(category.Id);
            ShowImportFileDialog(category.Id);
            return;
        }

        LibraryViewsListBox.Focus();
    }

    private async Task RenameCategoryFromEditorAsync(
        Guid categoryId,
        string categoryName)
    {
        if (!await libraryActionGate.WaitAsync(0))
        {
            CategoryEditorErrorTextBlock.Text =
                "Another library operation is already in progress.";
            return;
        }

        try
        {
            var renamed = await soundLibraryStore.RenameCategoryAsync(
                categoryId,
                categoryName);
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
        }
        catch (Exception exception)
        {
            CategoryEditorErrorTextBlock.Text = exception.Message;
            return;
        }
        finally
        {
            libraryActionGate.Release();
        }

        CloseCategoryEditor();
        LibraryViewsListBox.Focus();
    }

    // ---- Category commands ------------------------------------------------

    private void RenameCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SelectedLibraryView is
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            } selected)
        {
            BeginRenameCategory(categoryId, selected.DisplayName);
        }
    }

    private void RenameCategoryMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (TryGetUserCategoryFromMenu(sender, out var view))
        {
            BeginRenameCategory(view.CategoryId!.Value, view.DisplayName);
        }
    }

    private async void MoveCategoryEarlierMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (TryGetUserCategoryFromMenu(sender, out var view))
        {
            await MoveCategoryAsync(view.CategoryId!.Value, -1);
        }
    }

    private async void MoveCategoryLaterMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (TryGetUserCategoryFromMenu(sender, out var view))
        {
            await MoveCategoryAsync(view.CategoryId!.Value, 1);
        }
    }

    private async void DeleteCategoryMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (TryGetUserCategoryFromMenu(sender, out var view))
        {
            await ConfirmAndDeleteCategoryAsync(
                view.CategoryId!.Value,
                view.DisplayName);
        }
    }

    private static bool TryGetUserCategoryFromMenu(
        object sender,
        out LibraryViewItem view)
    {
        view = null!;
        if ((sender as FrameworkElement)?.Tag
            is not LibraryViewItem candidate
            || !candidate.IsUserCategory
            || candidate.CategoryId is null)
        {
            return false;
        }

        view = candidate;
        return true;
    }

    /// <summary>
    /// Built-in views cannot be renamed, reordered, or deleted, so they get
    /// no context menu at all rather than a menu of dead commands.
    /// </summary>
    private void LibraryViewItem_ContextMenuOpening(
        object sender,
        ContextMenuEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not LibraryViewItem { IsUserCategory: true })
        {
            eventArgs.Handled = true;
        }
    }

    private async void DeleteCategoryButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (SelectedLibraryView is
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            } selected)
        {
            await ConfirmAndDeleteCategoryAsync(
                categoryId,
                selected.DisplayName);
        }
    }

    private async Task ConfirmAndDeleteCategoryAsync(
        Guid categoryId,
        string displayName)
    {
        var assignedCount = soundTiles.Count(
            tile => tile.Sound.CategoryId == categoryId);
        var confirmation = MessageBox.Show(
            this,
            $"Delete the category \"{displayName}\"?"
            + Environment.NewLine
            + Environment.NewLine
            + $"{assignedCount} sound(s) currently in it will move to "
            + "Uncategorized. No sound is removed from the library and no "
            + "audio file is deleted — only the category itself goes away.",
            "Delete category",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (categoryEditorMode == CategoryEditorMode.Rename
            && categoryEditorCategoryId == categoryId)
        {
            CloseCategoryEditor();
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
                $"Deleted category \"{displayName}\" and moved "
                + $"{result.UncategorizedSoundCount} sound(s) to "
                + "Uncategorized. No audio files were deleted.";
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
        if (SelectedLibraryView is
            {
                Kind: SoundLibraryViewKind.Category,
                CategoryId: { } categoryId
            })
        {
            await MoveCategoryAsync(categoryId, offset);
        }
    }

    private async Task MoveCategoryAsync(Guid categoryId, int offset)
    {
        var currentIndex = soundCategories
            .Select((category, index) => (category, index))
            .Where(item => item.category.Id == categoryId)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        var newIndex = currentIndex + offset;
        if (currentIndex < 0
            || newIndex < 0
            || newIndex >= soundCategories.Count)
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
        UpdateSelectionPresentation();
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

    // ---- Dragging sounds --------------------------------------------------

    /// <summary>
    /// The explicit reorder handle. It uses the standard system drag
    /// threshold because grabbing it is already a deliberate gesture.
    /// </summary>
    private void SoundDragHandle_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        BeginTrackingSoundDrag(sender, eventArgs);
    }

    private void SoundDragHandle_PreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        TryStartSoundDrag(sender, eventArgs, thresholdScale: 1d);
    }

    /// <summary>
    /// The tile body is draggable too, which is what makes "drag a sound onto
    /// a category" work without hunting for the handle. The threshold is
    /// doubled so a slightly unsteady click still plays the sound instead of
    /// starting a drag.
    /// </summary>
    private void SoundTile_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        BeginTrackingSoundDrag(sender, eventArgs);
    }

    private void SoundTile_PreviewMouseMove(
        object sender,
        MouseEventArgs eventArgs)
    {
        TryStartSoundDrag(sender, eventArgs, thresholdScale: 2d);
    }

    private void BeginTrackingSoundDrag(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile
            || librarySelection.IsActive)
        {
            soundDragStartPoint = null;
            draggedSoundId = null;
            return;
        }

        soundDragStartPoint = eventArgs.GetPosition(this);
        draggedSoundId = tile.Id;
    }

    private void TryStartSoundDrag(
        object sender,
        MouseEventArgs eventArgs,
        double thresholdScale)
    {
        if (eventArgs.LeftButton != MouseButtonState.Pressed
            || soundDragStartPoint is not { } startPoint
            || draggedSoundId is not { } soundId
            || (sender as FrameworkElement)?.Tag
                is not SoundTileViewModel tile
            || tile.Id != soundId)
        {
            return;
        }

        var currentPoint = eventArgs.GetPosition(this);
        if (Math.Abs(currentPoint.X - startPoint.X)
                < SystemParameters.MinimumHorizontalDragDistance
                    * thresholdScale
            && Math.Abs(currentPoint.Y - startPoint.Y)
                < SystemParameters.MinimumVerticalDragDistance
                    * thresholdScale)
        {
            return;
        }

        soundDragStartPoint = null;
        var soundIds = SoundIdsForTileCommand(tile);
        var data = new DataObject();
        data.SetData(
            SoundDragDataFormat,
            string.Join(
                ";",
                soundIds.Select(id => id.ToString("D"))));
        var previousStatus = StatusTextBlock.Text;
        StatusTextBlock.Text = soundIds.Count == 1
            ? $"Dragging \"{tile.DisplayName}\". Drop it on a category to "
                + "file it, or between tiles to reorder."
            : $"Dragging {soundIds.Count} sounds. Drop them on a category "
                + "to file them together.";
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
            SetActiveDropTarget(null);
            if (StatusTextBlock.Text.StartsWith(
                    "Dragging",
                    StringComparison.Ordinal))
            {
                StatusTextBlock.Text = previousStatus;
            }
        }
    }

    private static IReadOnlyList<Guid> ReadDraggedSoundIds(IDataObject data)
    {
        if (data.GetData(SoundDragDataFormat) is not string payload)
        {
            return [];
        }

        var soundIds = new List<Guid>();
        foreach (var part in payload.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(part, out var soundId))
            {
                soundIds.Add(soundId);
            }
        }

        return soundIds;
    }

    /// <summary>
    /// A command started from one tile applies to the whole selection when
    /// that tile is part of it, and to that tile alone otherwise.
    /// </summary>
    private IReadOnlyList<Guid> SoundIdsForTileCommand(
        SoundTileViewModel tile)
    {
        return librarySelection.IsActive
            && librarySelection.IsSelected(tile.Id)
                ? SelectedSoundIds()
                : [tile.Id];
    }

    // ---- Reordering inside the grid ---------------------------------------

    private void SoundTile_DragOver(
        object sender,
        DragEventArgs eventArgs)
    {
        var canDrop = CanReorderSounds
            && eventArgs.Data.GetDataPresent(SoundDragDataFormat);
        eventArgs.Effects = canDrop
            ? DragDropEffects.Move
            : DragDropEffects.None;
        eventArgs.Handled = true;
        SetTileDropHighlight(sender, canDrop);
    }

    private void SoundTile_DragLeave(
        object sender,
        DragEventArgs eventArgs)
    {
        SetTileDropHighlight(sender, isDropTarget: false);
    }

    /// <summary>
    /// Uses a local value only while a drop is possible and clears it
    /// afterwards, so the tile's playing, selected, and hover triggers keep
    /// owning the border the rest of the time.
    /// </summary>
    private void SetTileDropHighlight(object sender, bool isDropTarget)
    {
        if (sender is not Border border)
        {
            return;
        }

        if (isDropTarget)
        {
            border.SetValue(
                Border.BorderBrushProperty,
                ThemeBrush("AccentBrush"));
        }
        else
        {
            border.ClearValue(Border.BorderBrushProperty);
        }
    }

    private async void SoundTile_Drop(
        object sender,
        DragEventArgs eventArgs)
    {
        SetTileDropHighlight(sender, isDropTarget: false);

        try
        {
            if (!CanReorderSounds
                || (sender as FrameworkElement)?.Tag
                    is not SoundTileViewModel target)
            {
                return;
            }

            var draggedIds = ReadDraggedSoundIds(eventArgs.Data);
            if (draggedIds.Count != 1)
            {
                return;
            }

            var sourceId = draggedIds[0];
            if (sourceId == target.Id)
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

    // ---- Dropping onto a sidebar view -------------------------------------

    private void LibraryViewItem_DragOver(
        object sender,
        DragEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not LibraryViewItem view)
        {
            return;
        }

        var draggingSounds =
            eventArgs.Data.GetDataPresent(SoundDragDataFormat);
        var draggingFiles = eventArgs.Data.GetDataPresent(
            DataFormats.FileDrop);
        var effects = DragDropEffects.None;
        if (draggingSounds && view.AcceptsSoundDrops)
        {
            effects = DragDropEffects.Move;
        }
        else if (draggingFiles && view.AcceptsFileDrops && !isImporting)
        {
            effects = DragDropEffects.Copy;
        }

        eventArgs.Effects = effects;
        eventArgs.Handled = true;
        SetActiveDropTarget(
            effects == DragDropEffects.None ? null : view);
        if (effects != DragDropEffects.None)
        {
            StatusTextBlock.Text = draggingSounds
                ? $"Release to move into {view.DisplayName}."
                : $"Release to import into {view.DisplayName}.";
        }
    }

    private void LibraryViewItem_DragLeave(
        object sender,
        DragEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is LibraryViewItem view
            && ReferenceEquals(activeDropTarget, view))
        {
            SetActiveDropTarget(null);
        }
    }

    private async void LibraryViewItem_Drop(
        object sender,
        DragEventArgs eventArgs)
    {
        SetActiveDropTarget(null);
        if ((sender as FrameworkElement)?.Tag
            is not LibraryViewItem view)
        {
            return;
        }

        eventArgs.Handled = true;
        if (view.AcceptsFileDrops
            && eventArgs.Data.GetData(DataFormats.FileDrop)
                is string[] paths)
        {
            RememberImportDestination(view.CategoryId);
            await ImportPathsFromUiAsync(paths, view.CategoryId);
            return;
        }

        if (!view.AcceptsSoundDrops)
        {
            return;
        }

        var soundIds = ReadDraggedSoundIds(eventArgs.Data);
        if (soundIds.Count > 0)
        {
            await MoveSoundsToCategoryAsync(soundIds, view.CategoryId);
        }
    }

    private void SetActiveDropTarget(LibraryViewItem? view)
    {
        if (ReferenceEquals(activeDropTarget, view))
        {
            return;
        }

        if (activeDropTarget is not null)
        {
            activeDropTarget.IsDropTarget = false;
        }

        activeDropTarget = view;
        if (activeDropTarget is not null)
        {
            activeDropTarget.IsDropTarget = true;
        }
    }

    // ---- Category assignment ----------------------------------------------

    /// <summary>
    /// The one place the application changes a sound's category. Drag drops,
    /// the tile quick menu, and the bulk command bar all call this, so the
    /// persistence, undo, and status behaviour can never drift apart.
    /// </summary>
    private async Task MoveSoundsToCategoryAsync(
        IReadOnlyList<Guid> soundIds,
        Guid? categoryId)
    {
        if (soundIds.Count == 0)
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
            var result = await soundLibraryStore.MoveToCategoryAsync(
                soundIds,
                categoryId);
            ApplyPersistedSoundOrder(result.Sounds);
            librarySelection.Retain(soundTiles.Select(tile => tile.Id));
            soundTilesView.Refresh();
            UpdateSelectionPresentation();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;

            var destinationName = DescribeImportDestination(categoryId);
            if (result.MovedCount == 0)
            {
                StatusTextBlock.Text = soundIds.Count == 1
                    ? $"That sound is already in {destinationName}."
                    : $"Those sounds are already in {destinationName}.";
                return;
            }

            var message = result.MovedCount == 1
                ? $"Moved to {destinationName}"
                : $"Moved {result.MovedCount} sounds to {destinationName}";
            StatusTextBlock.Text = $"{message}.";
            ShowUndoNotification(message, result.Undo);
            FocusAfterLibraryMutation(result.MovedSoundIds[0]);
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The category could not be changed: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private void ShowUndoNotification(
        string message,
        SoundCategoryMoveUndo undo)
    {
        lastMoveUndo = undo.CanUndo ? undo : null;
        UndoNotificationTextBlock.Text = message;
        UndoActionButton.IsEnabled = lastMoveUndo is not null;
        UndoNotificationBar.Visibility = Visibility.Visible;
        undoNotificationTimer.Stop();
        undoNotificationTimer.Start();
    }

    private void HideUndoNotification()
    {
        undoNotificationTimer.Stop();
        UndoNotificationBar.Visibility = Visibility.Collapsed;
        lastMoveUndo = null;
    }

    private void UndoNotificationTimer_Tick(object? sender, EventArgs eventArgs)
    {
        HideUndoNotification();
    }

    private void DismissUndoButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        HideUndoNotification();
    }

    private async void UndoActionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (lastMoveUndo is not { } undo)
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
            var restored = await soundLibraryStore
                .RestoreCategoryAssignmentsAsync(undo);
            ApplyPersistedSoundOrder(restored);
            soundTilesView.Refresh();
            UpdateSelectionPresentation();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text =
                "Undid the move. The previous categories and order were "
                + "restored.";
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The move could not be undone: {exception.Message}");
        }
        finally
        {
            libraryActionGate.Release();
            HideUndoNotification();
        }
    }

    // ---- Move-to menus ----------------------------------------------------

    private void MoveToCategoryMenuItem_SubmenuOpened(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is not MenuItem menuItem
            || menuItem.Tag is not SoundTileViewModel tile)
        {
            return;
        }

        var soundIds = SoundIdsForTileCommand(tile);
        var currentCategoryId = SharedCategoryId(
            soundIds,
            out var hasSharedCategory);
        menuItem.Items.Clear();
        foreach (var item in BuildCategoryChoiceItems(
            currentCategoryId,
            hasSharedCategory,
            " (current)",
            disableMarked: true,
            categoryId => _ = MoveSoundsToCategoryAsync(
                soundIds,
                categoryId),
            () => BeginCreateCategory(soundIds, pendingImport: false)))
        {
            menuItem.Items.Add(item);
        }
    }

    private void ShowMoveToCategoryMenu(
        FrameworkElement placementTarget,
        IReadOnlyList<Guid> soundIds)
    {
        var currentCategoryId = SharedCategoryId(
            soundIds,
            out var hasSharedCategory);
        var menu = new ContextMenu
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom
        };
        menu.Items.Add(
            new MenuItem
            {
                Header = soundIds.Count == 1
                    ? "Move this sound to…"
                    : $"Move {soundIds.Count} sounds to…",
                IsEnabled = false
            });
        menu.Items.Add(new Separator());
        foreach (var item in BuildCategoryChoiceItems(
            currentCategoryId,
            hasSharedCategory,
            " (current)",
            disableMarked: true,
            categoryId => _ = MoveSoundsToCategoryAsync(
                soundIds,
                categoryId),
            () => BeginCreateCategory(soundIds, pendingImport: false)))
        {
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// The category every listed sound already shares, if there is one.
    /// A mixed selection has none, so nothing gets marked as current.
    /// </summary>
    private Guid? SharedCategoryId(
        IReadOnlyList<Guid> soundIds,
        out bool hasSharedCategory)
    {
        var categoryIds = soundIds
            .Select(soundId => FindTile(soundId)?.Sound.CategoryId)
            .ToArray();
        hasSharedCategory = categoryIds.Length > 0
            && categoryIds.Distinct().Count() == 1;
        return hasSharedCategory ? categoryIds[0] : null;
    }

    private List<Control> BuildCategoryChoiceItems(
        Guid? markedCategoryId,
        bool hasMarkedCategory,
        string markedSuffix,
        bool disableMarked,
        Action<Guid?> onChosen,
        Action onCreateCategory)
    {
        var items = new List<Control>();
        void AddChoice(Guid? categoryId, string displayName)
        {
            var marked = hasMarkedCategory
                && markedCategoryId == categoryId;
            items.Add(
                CreateCategoryMenuItem(
                    marked ? displayName + markedSuffix : displayName,
                    !(marked && disableMarked),
                    () => onChosen(categoryId)));
        }

        AddChoice(null, "Uncategorized");
        foreach (var category in soundCategories
                     .OrderBy(category => category.SortOrder))
        {
            AddChoice(category.Id, category.DisplayName);
        }

        items.Add(new Separator());
        items.Add(
            CreateCategoryMenuItem(
                "Create category…",
                isEnabled: true,
                onCreateCategory));
        return items;
    }

    private static MenuItem CreateCategoryMenuItem(
        string header,
        bool isEnabled,
        Action invoke)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = isEnabled
        };
        System.Windows.Automation.AutomationProperties.SetName(item, header);
        item.Click += (_, _) => invoke();
        return item;
    }

    // ---- Organization mode ------------------------------------------------

    private void OrganizeToggleButton_Checked(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (librarySelection.IsActive)
        {
            return;
        }

        librarySelection.Activate();
        UpdateSelectionPresentation();
        StatusTextBlock.Text =
            "Organization mode. Select tiles, then use the command bar. "
            + "Ctrl+A selects everything in this view, Escape leaves.";
    }

    private void OrganizeToggleButton_Unchecked(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (librarySelection.IsActive)
        {
            ExitSelectionMode();
        }
    }

    private void ExitSelectionMode()
    {
        librarySelection.Deactivate();
        UpdateSelectionPresentation();
    }

    private void CancelSelectionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        ExitSelectionMode();
        StatusTextBlock.Text = "Left organization mode.";
    }

    private void SelectAllButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        librarySelection.SelectAll(VisibleSoundIds());
        UpdateSelectionPresentation();
    }

    private void SelectTileMenuItem_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if ((sender as FrameworkElement)?.Tag
            is not SoundTileViewModel tile)
        {
            return;
        }

        librarySelection.Select(tile.Id);
        UpdateSelectionPresentation();
    }

    private void MoveSelectionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var soundIds = SelectedSoundIds();
        if (soundIds.Count > 0)
        {
            ShowMoveToCategoryMenu(
                sender as FrameworkElement ?? MoveSelectionButton,
                soundIds);
        }
    }

    private async void FavoriteSelectionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await SetFavoriteForSelectionAsync(isFavorite: true);
    }

    private async void UnfavoriteSelectionButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        await SetFavoriteForSelectionAsync(isFavorite: false);
    }

    private async Task SetFavoriteForSelectionAsync(bool isFavorite)
    {
        var soundIds = SelectedSoundIds();
        if (soundIds.Count == 0)
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
            var updated = await soundLibraryStore.SetFavoriteAsync(
                soundIds,
                isFavorite);
            ApplyPersistedSoundOrder(updated);
            librarySelection.Retain(soundTiles.Select(tile => tile.Id));
            soundTilesView.Refresh();
            UpdateSelectionPresentation();
            UpdateLibraryPresentation();
            ErrorTextBlock.Text = string.Empty;
            StatusTextBlock.Text = isFavorite
                ? $"Added {soundIds.Count} sound(s) to Favorites."
                : $"Removed {soundIds.Count} sound(s) from Favorites.";
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"The favorite state could not be saved: "
                + exception.Message);
        }
        finally
        {
            libraryActionGate.Release();
        }
    }

    private IReadOnlyList<Guid> VisibleSoundIds()
    {
        return soundTilesView
            .Cast<SoundTileViewModel>()
            .Select(tile => tile.Id)
            .ToArray();
    }

    private IReadOnlyList<Guid> SelectedSoundIds()
    {
        return librarySelection.InVisualOrder(
            soundTiles.Select(tile => tile.Id).ToArray());
    }

    private void UpdateSelectionPresentation()
    {
        var isActive = librarySelection.IsActive;
        var selectedCount = librarySelection.Count;
        OrganizeToggleButton.IsChecked = isActive;
        SelectionCommandBar.Visibility = isActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        SelectionCountTextBlock.Text = selectedCount == 0
            ? "Select sounds to move or favorite them together"
            : librarySelection.SelectionCountText;
        MoveSelectionButton.IsEnabled = selectedCount > 0;
        FavoriteSelectionButton.IsEnabled = selectedCount > 0;
        UnfavoriteSelectionButton.IsEnabled = selectedCount > 0;
        SelectAllButton.IsEnabled = isActive && soundTiles.Count > 0;

        foreach (var tile in soundTiles)
        {
            tile.IsSelectionMode = isActive;
            tile.IsSelected = isActive && librarySelection.IsSelected(tile.Id);
        }
    }

    private void MainWindow_PreviewKeyDown(
        object sender,
        KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            if (categoryEditorMode != CategoryEditorMode.Hidden)
            {
                CloseCategoryEditor();
                LibraryViewsListBox.Focus();
                eventArgs.Handled = true;
            }
            else if (librarySelection.IsActive)
            {
                ExitSelectionMode();
                StatusTextBlock.Text = "Left organization mode.";
                eventArgs.Handled = true;
            }

            return;
        }

        if (eventArgs.Key == Key.A
            && Keyboard.Modifiers == ModifierKeys.Control
            && Keyboard.FocusedElement is not TextBoxBase)
        {
            librarySelection.SelectAll(VisibleSoundIds());
            UpdateSelectionPresentation();
            eventArgs.Handled = true;
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
        if (!ObserveCurrentAudioEngineSession(eventArgs.SessionId))
        {
            return;
        }

        RunOnUiThread(
            () =>
            {
                if (eventArgs.SessionId
                    != Interlocked.Read(ref currentAudioEngineSessionId))
                {
                    return;
                }

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
        EngineStateTextBlock.Text = state switch
        {
            AudioEngineState.Running => "Ready",
            AudioEngineState.Starting => "Connecting",
            AudioEngineState.Stopping => "Reconnecting",
            AudioEngineState.Faulted => "Unavailable",
            _ => "Waiting"
        };
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

        MicrophoneStateTextBlock.Text = state switch
        {
            AudioEngineState.Running => "Microphone live",
            AudioEngineState.Faulted => "Microphone released",
            _ => "Microphone idle"
        };
        MicrophoneStateTextBlock.Foreground = ThemeBrush("TextMutedBrush");
    }

    /// <summary>
    /// Keeps the compact bottom-bar chips current: monitoring state, the
    /// most recent global-hotkey action.
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

    }

    private void AudioEngine_ErrorOccurred(
        object? sender,
        AudioEngineErrorEventArgs eventArgs)
    {
        if (!ObserveCurrentAudioEngineSession(eventArgs.SessionId))
        {
            return;
        }

        RunOnUiThread(
            () =>
            {
                if (eventArgs.SessionId
                    != Interlocked.Read(ref currentAudioEngineSessionId))
                {
                    return;
                }

                lastDiagnosticMessage = eventArgs.Message;
                ErrorTextBlock.Text = eventArgs.Message;

                if (!eventArgs.IsRecoverable)
                {
                    StatusTextBlock.Text =
                        "Audio is temporarily unavailable. Soundboard will reconnect when the device is ready.";
                }

                UpdateMonitorStatusForSelection();
                RefreshDiagnosticStatus();
            });
    }

    private bool ObserveCurrentAudioEngineSession(long sessionId)
    {
        while (true)
        {
            var currentSessionId = Interlocked.Read(
                ref currentAudioEngineSessionId);
            if (sessionId < currentSessionId)
            {
                return false;
            }

            if (sessionId == currentSessionId
                || Interlocked.CompareExchange(
                    ref currentAudioEngineSessionId,
                    sessionId,
                    currentSessionId) == currentSessionId)
            {
                return true;
            }
        }
    }

    private void AudioEngine_PeakLevelsChanged(
        object? sender,
        AudioPeakLevelsEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                var microphonePeak = SanitizePeak(eventArgs.MicrophonePeak);
                var mixedOutputPeak = SanitizePeak(eventArgs.MixedOutputPeak);
                var monitorOutputPeak = SanitizePeak(eventArgs.MonitorOutputPeak);
                MicrophonePeakProgressBar.Value = microphonePeak;
                OutputPeakProgressBar.Value = mixedOutputPeak;
                MicrophonePeakTextBlock.Text =
                    $"{microphonePeak:P0}";
                OutputPeakTextBlock.Text =
                    $"{mixedOutputPeak:P0}";
                MonitorPeakProgressBar.Value = monitorOutputPeak;
                MonitorPeakTextBlock.Text =
                    $"{monitorOutputPeak:P0}";

                // Informational only. The meter notifications already arrive
                // from the engine, so the live status needs no UI timer and
                // the audio path never waits for WPF.
                VoicePriorityStatusTextBlock.Visibility =
                    eventArgs.VoiceDuckingActive
                        ? Visibility.Visible
                        : Visibility.Collapsed;
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
                    var remainingSoundId = audioEngine.CurrentSoundId;
                    if (remainingSoundId is { } remainingId)
                    {
                        currentSoundId = remainingId;
                        currentSoundSessionId =
                            audioEngine.Diagnostics?.CurrentPlaybackSessionId
                            ?? currentSoundSessionId;
                        SetPlayingTile(remainingId);
                        var remainingName = FindTile(remainingId)?.DisplayName
                            ?? remainingId.ToString();
                        CurrentSoundTextBlock.Text =
                            $"Playing {remainingName} · one-shot";
                    }
                    else
                    {
                        currentSoundId = null;
                        SetPlayingTile(null);
                        CurrentSoundTextBlock.Text = "No sound playing";
                    }

                    if (audioEngine.State == AudioEngineState.Running)
                    {
                        StatusTextBlock.Text = remainingSoundId is not null
                            ? $"{finishedName} ended; other triggered sounds are still playing."
                            : eventArgs.Reason switch
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
                return "Clear the search to reorder. Dragging onto a "
                    + "category still works.";
            }

            if (SelectedLibraryView?.Kind
                == SoundLibraryViewKind.Favorites)
            {
                return "Ordering is unavailable in Favorites. Dragging onto "
                    + "a category still works.";
            }

            return "Drag onto a category to file this sound, or between "
                + "tiles to reorder.";
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
        var isFirstUserCategory = true;
        foreach (var category in soundCategories
                     .OrderBy(category => category.SortOrder))
        {
            libraryViews.Add(
                new LibraryViewItem(
                    SoundLibraryViewKind.Category,
                    category.DisplayName,
                    category.Id)
                {
                    StartsUserCategorySection = isFirstUserCategory
                });
            isFirstUserCategory = false;
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
        var selectorsCanChange = !isRefreshing
            && state is not (AudioEngineState.Starting or AudioEngineState.Stopping);

        MicrophoneComboBox.IsEnabled = selectorsCanChange
            && UseDefaultMicrophoneCheckBox.IsChecked != true;
        VirtualOutputComboBox.IsEnabled = selectorsCanChange;
        MonitorSoundsCheckBox.IsEnabled = selectorsCanChange;
        MonitorOutputComboBox.IsEnabled = selectorsCanChange;
        RefreshDevicesButton.IsEnabled = !isRefreshing;

        var volumeControlsEnabled =
            state is AudioEngineState.Stopped or AudioEngineState.Running;
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
        UpdatePlaybackTransportPresentation(state);
        UpdateCategoryControlAvailability();
        UpdateMonitorStatusForSelection();
        UpdateStatusChips();
    }

    /// <summary>
    /// Keeps the transport button in step with the one global paused state.
    /// The button is disabled whenever there is no session to pause, and the
    /// interface returns to its normal unpaused state once every session
    /// finishes or is stopped.
    /// </summary>
    private void UpdatePlaybackTransportPresentation(AudioEngineState state)
    {
        var paused = audioEngine.IsPlaybackPaused;
        PausePlaybackButton.IsEnabled =
            state == AudioEngineState.Running
            && audioEngine.CanPausePlayback;
        PausePlaybackTextBlock.Text = paused
            ? "Resume sounds"
            : "Pause sounds";
        PausePlaybackGlyph.Text = paused
            ? "\uE768"
            : "\uE769";
        PausePlaybackButton.ToolTip = paused
            ? "Resume every paused sound from its exact position."
            : "Pause the sounds that are playing. The microphone keeps running.";
        AutomationProperties.SetName(
            PausePlaybackButton,
            paused ? "Resume sounds" : "Pause sounds");
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

        // The category chip only earns its space where the grid actually
        // mixes categories.
        var showCategoryChips =
            !string.IsNullOrWhiteSpace(SearchTextBox?.Text)
            || selectedView?.Kind is SoundLibraryViewKind.AllSounds
                or SoundLibraryViewKind.Favorites;
        foreach (var tile in soundTiles)
        {
            tile.CanReorder = canReorder;
            tile.ReorderAvailabilityText = availabilityText;
            tile.ShowCategoryChip = showCategoryChips;
        }

        ImportButtonTextBlock.Text =
            selectedView?.Kind == SoundLibraryViewKind.Category
                ? "Import here"
                : "Import";
        ImportSoundsButton.ToolTip =
            selectedView?.Kind == SoundLibraryViewKind.Category
                ? $"Import WAV, MP3, or Ogg files straight into "
                    + $"{selectedView.DisplayName}"
                : "Import WAV, MP3, Ogg Opus, or Ogg Vorbis files and "
                    + "choose where they go";
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
                + "quick access. Organize mode can favorite several sounds "
                + "at once.";
        }
        else
        {
            emptyStateAction = EmptyStateAction.Import;
            EmptyStateGlyph.Text = ThemeGlyph("GlyphVolume");
            EmptyStateTitleTextBlock.Text =
                $"Nothing in {selectedView?.DisplayName ?? "this view"}";
            EmptyStateMessageTextBlock.Text =
                "Import sounds straight into this category, or drag existing "
                + "tiles onto it from All Sounds.";
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
            PauseHotkeyDisplayTextBlock.Text =
                appSettings.PauseResumeHotkey?.DisplayText ?? "No hotkey";
            PauseHotkeyStateTextBlock.Text =
                appSettings.PauseResumeHotkey is null
                    ? "Not assigned"
                    : "Assigned · registration pending";
            ClearPauseHotkeyButton.IsEnabled =
                appSettings.PauseResumeHotkey is not null;
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

        var pauseStatus =
            hotkeyService.GetStatus(HotkeyTarget.PauseResumePlayback);
        PauseHotkeyDisplayTextBlock.Text =
            pauseStatus.Hotkey?.DisplayText ?? "No hotkey";
        PauseHotkeyStateTextBlock.Text = pauseStatus.State switch
        {
            HotkeyRegistrationState.Registered =>
                "Assigned · registered",
            HotkeyRegistrationState.Unavailable =>
                "Assigned · unavailable",
            HotkeyRegistrationState.Disabled =>
                "Assigned · global hotkeys disabled",
            _ => "Not assigned"
        };
        PauseHotkeyStateTextBlock.ToolTip = pauseStatus.Error;
        ClearPauseHotkeyButton.IsEnabled = pauseStatus.Hotkey is not null;

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
            + $"{monitor.FriendlyName}. Changes reconnect automatically.";
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
        var pauseHotkeyStatus = hotkeyService?.GetStatus(
            HotkeyTarget.PauseResumePlayback);
        var voicePrioritySettings = audioEngine.VoicePriority;

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
            $"Microphone selection mode: "
                + (appSettings.UseDefaultMicrophone
                    ? "Windows default communications microphone"
                    : "Pinned endpoint"),
            $"Sound master gain: {AudioGain.FromPercent(appSettings.SoundVolume * 100d):P0}",
            $"Playback paused: {YesNo(audioEngine.IsPlaybackPaused)}",
            $"Voice Priority enabled: "
                + $"{YesNo(appSettings.VoicePriorityEnabled)}",
            $"Voice Priority sensitivity: "
                + $"{appSettings.VoicePrioritySensitivity} "
                + $"({voicePrioritySettings.EngageThresholdDb:0} dBFS engage, "
                + $"{voicePrioritySettings.DisengageThresholdDb:0} dBFS release)",
            $"Voice Priority strength: {appSettings.VoicePriorityStrength} "
                + $"({voicePrioritySettings.DuckingDb:0} dB, "
                + $"gain {voicePrioritySettings.DuckingGain:0.000})",
            $"Voice Priority currently lowering sounds: "
                + $"{YesNo(audioEngine.IsVoiceDuckingActive)}",
            $"Pause/Resume hotkey state: "
                + $"{pauseHotkeyStatus?.State.ToString() ?? "NotAssigned"}"
                + (pauseHotkeyStatus?.Hotkey is null
                    ? string.Empty
                    : $" ({pauseHotkeyStatus.Hotkey.DisplayText})"),
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
                $"- Active sound sessions: {engineDiagnostics.ActiveSoundCount}");
            lines.Add(
                $"- Final-boundary clipped samples: {engineDiagnostics.ClippedSampleCount}");
            lines.Add(
                $"- Final-boundary non-finite samples rejected: {engineDiagnostics.NonFiniteSampleCount}");
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
            $"- Preview final-boundary clipped samples: "
            + $"{previewService.ClippedSampleCount}");
        lines.Add(
            $"- Preview final-boundary non-finite samples rejected: "
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

    /// <summary>
    /// Only the files that need attention. Successful imports are already
    /// reported by the status summary, so listing them again on the error
    /// line would make a clean import look like a problem.
    /// </summary>
    private static string BuildImportDetails(SoundImportResult result)
    {
        var details = new List<string>();
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
            UseDefaultMicrophone =
                UseDefaultMicrophoneCheckBox.IsChecked == true,
            MicrophoneEndpointId = pinnedMicrophoneEndpointId,
            VirtualOutputEndpointId = configuredVirtualOutputEndpointId,
            MonitoringEnabled =
                MonitorSoundsCheckBox.IsChecked == true,
            MonitorOutputEndpointId =
                (MonitorOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId,
            MonitorVolume = MonitorVolumeSlider.Value / 100d,
            GlobalHotkeysEnabled =
                GlobalHotkeysCheckBox.IsChecked == true,
            VoicePriorityEnabled =
                VoicePriorityCheckBox.IsChecked == true,
            VoicePrioritySensitivity =
                CurrentVoicePrioritySettings.Sensitivity,
            VoicePriorityStrength =
                CurrentVoicePrioritySettings.Strength,
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
        deviceChangeDebounceCancellation?.Cancel();
        deviceChangeDebounceCancellation?.Dispose();
        deviceChangeDebounceCancellation = null;
        SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
        audioDeviceService.DevicesChanged -= AudioDeviceService_DevicesChanged;

        var shutdownErrors = new List<string>();

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
            await audioServiceGate.WaitAsync();
            try
            {
                await audioServiceLifecycle.StopAsync();
                await Task.Run(audioEngine.Dispose);
            }
            finally
            {
                audioServiceGate.Release();
            }
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
        audioDeviceService.Dispose();
        await soundLibraryStore.DisposeAsync();
        await settingsStore.DisposeAsync();
        libraryActionGate.Dispose();
        soundTriggerGate.Dispose();
        audioServiceGate.Dispose();

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

    private static float SanitizePeak(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

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
    /// State of the inline sidebar name field.
    /// </summary>
    private enum CategoryEditorMode
    {
        Hidden,
        Create,
        Rename
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
