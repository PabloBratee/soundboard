using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Soundboard.Audio;

namespace Soundboard.App;

public partial class MainWindow : Window
{
    private readonly AudioDeviceService audioDeviceService = new();
    private readonly AudioMixEngine audioEngine = new();
    private AudioDeviceSnapshot? currentSnapshot;
    private AudioFormatInfo? selectedMicrophoneFormat;
    private AudioFormatInfo? selectedRenderFormat;
    private string? selectedSoundFilePath;
    private string lastDiagnosticMessage = "No engine diagnostic messages.";
    private bool isRefreshing;
    private bool isClosing;
    private long formatRequestNumber;

    public MainWindow()
    {
        InitializeComponent();

        MicrophoneComboBox.SelectionChanged +=
            DeviceComboBox_SelectionChanged;
        VirtualOutputComboBox.SelectionChanged +=
            DeviceComboBox_SelectionChanged;
        MicrophoneVolumeSlider.ValueChanged +=
            MicrophoneVolumeSlider_ValueChanged;
        MuteMicrophoneCheckBox.Checked +=
            MuteMicrophoneCheckBox_Changed;
        MuteMicrophoneCheckBox.Unchecked +=
            MuteMicrophoneCheckBox_Changed;
        SoundVolumeSlider.ValueChanged +=
            SoundVolumeSlider_ValueChanged;

        audioEngine.StateChanged += AudioEngine_StateChanged;
        audioEngine.ErrorOccurred += AudioEngine_ErrorOccurred;
        audioEngine.PeakLevelsChanged += AudioEngine_PeakLevelsChanged;
        audioEngine.SoundPlaybackStateChanged +=
            AudioEngine_SoundPlaybackStateChanged;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        UpdateControlAvailability();
    }

    private async void MainWindow_Loaded(
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
                $"Initial audio-device discovery failed: {exception.Message}");
        }
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

    private async Task RefreshDevicesAsync()
    {
        if (audioEngine.State != AudioEngineState.Stopped)
        {
            ShowUiError(
                "Stop the audio engine before refreshing devices.");
            return;
        }

        var selectedCaptureId =
            (MicrophoneComboBox.SelectedItem as AudioEndpoint)?.DeviceId;
        var selectedRenderId =
            (VirtualOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId;

        isRefreshing = true;
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

            DeviceCountsTextBlock.Text =
                $"{snapshot.CaptureEndpoints.Count} active capture "
                + $"endpoint(s), {snapshot.RenderEndpoints.Count} active "
                + "render endpoint(s)";

            UpdateVirtualCableStatus(snapshot);
            StatusTextBlock.Text =
                "Device discovery completed. No Windows defaults were "
                + "changed.";
            lastDiagnosticMessage =
                snapshot.Warnings.Count == 0
                    ? "Device discovery completed without warnings."
                    : string.Join(" | ", snapshot.Warnings);

            await UpdateSelectedFormatsAsync();
        }
        finally
        {
            isRefreshing = false;
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

        AudioFormatInfo? microphoneFormat = null;
        AudioFormatInfo? renderFormat = null;

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

        if (requestNumber != Interlocked.Read(ref formatRequestNumber)
            || isClosing)
        {
            return;
        }

        selectedMicrophoneFormat = microphoneFormat;
        selectedRenderFormat = renderFormat;
        TargetFormatTextBlock.Text = renderFormat is null
            ? "Select a render endpoint."
            : $"{renderFormat.SampleRate:N0} Hz, "
                + $"{FormatChannelCount(renderFormat.Channels)}, "
                + "32-bit IEEE floating point mixer target "
                + $"(endpoint mix format: {renderFormat.SampleFormat})";
    }

    private async void StartEngineButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var microphone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;

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
                    render.DeviceId));

            StatusTextBlock.Text =
                "Audio engine is running and writing the mix to "
                + $"{render.FriendlyName}.";
            lastDiagnosticMessage =
                "The audio engine started successfully.";
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
    }

    private void MuteMicrophoneCheckBox_Changed(
        object sender,
        RoutedEventArgs eventArgs)
    {
        audioEngine.MicrophoneMuted =
            MuteMicrophoneCheckBox.IsChecked == true;
    }

    private void SoundVolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        var percent = (int)Math.Round(eventArgs.NewValue);
        SoundVolumeTextBlock.Text = $"{percent}%";
        audioEngine.SoundVolume = percent / 100f;
    }

    private void ChooseSoundFileButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a sound file",
            Filter = "Supported audio (*.wav;*.mp3)|*.wav;*.mp3"
                + "|WAV files (*.wav)|*.wav"
                + "|MP3 files (*.mp3)|*.mp3",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        selectedSoundFilePath = dialog.FileName;
        SelectedSoundFileTextBlock.Text =
            Path.GetFileName(dialog.FileName);
        SelectedSoundFileTextBlock.ToolTip = dialog.FileName;
        ErrorTextBlock.Text = string.Empty;
        StatusTextBlock.Text =
            $"Selected sound file: {Path.GetFileName(dialog.FileName)}";
        UpdateControlAvailability();
    }

    private async void PlaySoundButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (selectedSoundFilePath is null)
        {
            ShowUiError("Choose a WAV or MP3 file first.");
            return;
        }

        ErrorTextBlock.Text = string.Empty;

        try
        {
            await Task.Run(
                () => audioEngine.PlaySound(selectedSoundFilePath));
            StatusTextBlock.Text =
                $"Playing {Path.GetFileName(selectedSoundFilePath)} into "
                + "the microphone mix.";
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

    private async void StopSoundButton_Click(
        object sender,
        RoutedEventArgs eventArgs)
    {
        try
        {
            await Task.Run(audioEngine.StopSound);
            StatusTextBlock.Text =
                "Sound playback stopped. The microphone remains active.";
        }
        catch (Exception exception)
        {
            ShowUiError(
                $"Sound playback could not be stopped: "
                + exception.Message);
        }
        finally
        {
            UpdateControlAvailability();
        }
    }

    private void AudioEngine_StateChanged(
        object? sender,
        AudioEngineStateChangedEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                EngineStateTextBlock.Text = eventArgs.State.ToString();
                EngineStateTextBlock.Foreground =
                    eventArgs.State switch
                    {
                        AudioEngineState.Running => Brushes.DarkGreen,
                        AudioEngineState.Faulted => Brushes.DarkRed,
                        _ => Brushes.Black
                    };
                UpdateControlAvailability();
                RefreshDiagnosticStatus();
            });
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
            });
    }

    private void AudioEngine_SoundPlaybackStateChanged(
        object? sender,
        SoundPlaybackStateChangedEventArgs eventArgs)
    {
        RunOnUiThread(
            () =>
            {
                if (audioEngine.State == AudioEngineState.Running)
                {
                    StatusTextBlock.Text = eventArgs.Reason switch
                    {
                        SoundPlaybackChangeReason.Completed =>
                            "Sound playback finished. The microphone remains "
                            + "active.",
                        SoundPlaybackChangeReason.Stopped =>
                            "Sound playback stopped. The microphone remains "
                            + "active.",
                        _ => StatusTextBlock.Text
                    };
                }

                UpdateControlAvailability();
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
        ChooseSoundFileButton.IsEnabled =
            state is not (AudioEngineState.Starting
                or AudioEngineState.Stopping);
        PlaySoundButton.IsEnabled =
            state == AudioEngineState.Running
            && selectedSoundFilePath is not null
            && !audioEngine.IsSoundPlaying;
        StopSoundButton.IsEnabled =
            state == AudioEngineState.Running
            && audioEngine.IsSoundPlaying;
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
                "A complete VB-CABLE render/capture pair was not detected. "
                + "Installation or the required Windows restart appears "
                + "incomplete.";
            VirtualCableStatusTextBlock.Foreground = Brushes.DarkRed;
            return;
        }

        VirtualCableStatusTextBlock.Text =
            $"VB-CABLE detected: render \"{likelyRender.FriendlyName}\" "
            + $"and capture \"{likelyCapture.FriendlyName}\".";
        VirtualCableStatusTextBlock.Foreground = Brushes.DarkGreen;
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
    }

    private void RefreshDiagnosticStatus()
    {
        var lines = new List<string>
        {
            $"Last refresh: {DateTime.Now:G}",
            $"Engine state: {audioEngine.State}",
            "Windows defaults changed by app: No",
            string.Empty
        };

        var microphone =
            MicrophoneComboBox.SelectedItem as AudioEndpoint;
        var render =
            VirtualOutputComboBox.SelectedItem as AudioEndpoint;
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
        }

        lines.Add(
            $"- Microphone buffer overflows: "
            + $"{audioEngine.MicrophoneBufferOverflowCount}");
        lines.Add($"- Last diagnostic: {lastDiagnosticMessage}");

        if (currentSnapshot is not null)
        {
            lines.Add(string.Empty);
            lines.Add("All active capture endpoints:");
            AddEndpointLines(lines, currentSnapshot.CaptureEndpoints);
            lines.Add(string.Empty);
            lines.Add("All active render endpoints:");
            AddEndpointLines(lines, currentSnapshot.RenderEndpoints);

            if (currentSnapshot.Warnings.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Discovery warnings:");
                lines.AddRange(
                    currentSnapshot.Warnings.Select(
                        warning => $"- {warning}"));
            }
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

    private static void AddEndpointLines(
        ICollection<string> lines,
        IReadOnlyList<AudioEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            lines.Add("- None detected");
            return;
        }

        foreach (var endpoint in endpoints)
        {
            var marker = endpoint.IsLikelyVbCable
                ? " [likely VB-CABLE]"
                : endpoint.IsDefault
                    ? " [default]"
                    : string.Empty;
            lines.Add($"- {endpoint.FriendlyName}{marker}");
            lines.Add($"  ID: {endpoint.DeviceId}");
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

    private void MainWindow_Closed(
        object? sender,
        EventArgs eventArgs)
    {
        isClosing = true;
        audioEngine.StateChanged -= AudioEngine_StateChanged;
        audioEngine.ErrorOccurred -= AudioEngine_ErrorOccurred;
        audioEngine.PeakLevelsChanged -= AudioEngine_PeakLevelsChanged;
        audioEngine.SoundPlaybackStateChanged -=
            AudioEngine_SoundPlaybackStateChanged;
        audioEngine.Dispose();
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
}
