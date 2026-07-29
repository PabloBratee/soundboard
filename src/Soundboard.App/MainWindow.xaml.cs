using System.Windows;
using System.Windows.Media;
using Soundboard.Audio;

namespace Soundboard.App;

public partial class MainWindow : Window
{
    private readonly AudioDeviceService audioDeviceService = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        var selectedCaptureId = (MicrophoneComboBox.SelectedItem as AudioEndpoint)?.DeviceId;
        var selectedRenderId = (VirtualOutputComboBox.SelectedItem as AudioEndpoint)?.DeviceId;

        RefreshDevicesButton.IsEnabled = false;
        DeviceCountsTextBlock.Text = "Discovering active endpoints…";
        DiagnosticStatusTextBox.Text = "Querying Windows Core Audio endpoints…";

        try
        {
            var snapshot = await Task.Run(audioDeviceService.GetActiveDevices);

            MicrophoneComboBox.ItemsSource = snapshot.CaptureEndpoints;
            VirtualOutputComboBox.ItemsSource = snapshot.RenderEndpoints;

            MicrophoneComboBox.SelectedItem =
                FindById(snapshot.CaptureEndpoints, selectedCaptureId)
                ?? snapshot.CaptureEndpoints.FirstOrDefault(endpoint => endpoint.IsDefault)
                ?? snapshot.CaptureEndpoints.FirstOrDefault();

            var likelyVirtualCable = snapshot.RenderEndpoints
                .FirstOrDefault(endpoint => endpoint.IsLikelyVbCable);

            VirtualOutputComboBox.SelectedItem =
                FindById(snapshot.RenderEndpoints, selectedRenderId)
                ?? likelyVirtualCable
                ?? snapshot.RenderEndpoints.FirstOrDefault();

            DeviceCountsTextBlock.Text =
                $"{snapshot.CaptureEndpoints.Count} active capture endpoint(s), "
                + $"{snapshot.RenderEndpoints.Count} active render endpoint(s)";

            UpdateVirtualCableStatus(likelyVirtualCable);
            DiagnosticStatusTextBox.Text = BuildDiagnosticStatus(snapshot);
        }
        catch (Exception exception)
        {
            DeviceCountsTextBlock.Text = "Audio-device discovery failed.";
            VirtualCableStatusTextBlock.Text =
                "Could not determine whether a VB-CABLE render endpoint is available.";
            VirtualCableStatusTextBlock.Foreground = Brushes.DarkRed;
            DiagnosticStatusTextBox.Text =
                "Windows audio devices could not be enumerated.\n\n"
                + $"Details: {exception.Message}\n\n"
                + "No Windows audio settings were changed.";
        }
        finally
        {
            RefreshDevicesButton.IsEnabled = true;
        }
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

    private void UpdateVirtualCableStatus(AudioEndpoint? likelyVirtualCable)
    {
        if (likelyVirtualCable is null)
        {
            VirtualCableStatusTextBlock.Text =
                "No likely VB-CABLE render endpoint was detected. "
                + "The diagnostic screen remains usable without it.";
            VirtualCableStatusTextBlock.Foreground = Brushes.DarkGoldenrod;
            return;
        }

        VirtualCableStatusTextBlock.Text =
            $"Likely VB-CABLE render endpoint detected: {likelyVirtualCable.FriendlyName}";
        VirtualCableStatusTextBlock.Foreground = Brushes.DarkGreen;
    }

    private static string BuildDiagnosticStatus(AudioDeviceSnapshot snapshot)
    {
        var lines = new List<string>
        {
            $"Last refresh: {DateTime.Now:G}",
            "No audio stream was started and no Windows defaults were changed.",
            string.Empty,
            "Capture endpoints:"
        };

        AddEndpointLines(lines, snapshot.CaptureEndpoints);
        lines.Add(string.Empty);
        lines.Add("Render endpoints:");
        AddEndpointLines(lines, snapshot.RenderEndpoints);

        if (snapshot.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Warnings:");
            lines.AddRange(snapshot.Warnings.Select(warning => $"- {warning}"));
        }

        return string.Join(Environment.NewLine, lines);
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
            var markers = new List<string>();

            if (endpoint.IsDefault)
            {
                markers.Add("default");
            }

            if (endpoint.IsLikelyVbCable)
            {
                markers.Add("likely VB-CABLE");
            }

            var markerText = markers.Count == 0
                ? string.Empty
                : $" ({string.Join(", ", markers)})";

            lines.Add($"- {endpoint.FriendlyName}{markerText}");
        }
    }
}
