using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Soundboard.Audio;

public sealed class AudioDeviceService
{
    public AudioDeviceSnapshot GetActiveDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var warnings = new List<string>();
        var defaultCaptureDeviceId = GetDefaultCaptureDeviceId(enumerator, warnings);

        var captureEndpoints = EnumerateActiveEndpoints(
            enumerator,
            DataFlow.Capture,
            AudioDeviceDirection.Capture,
            defaultCaptureDeviceId,
            warnings);

        var renderEndpoints = EnumerateActiveEndpoints(
            enumerator,
            DataFlow.Render,
            AudioDeviceDirection.Render,
            defaultDeviceId: null,
            warnings);

        return new AudioDeviceSnapshot(
            Array.AsReadOnly(captureEndpoints),
            Array.AsReadOnly(renderEndpoints),
            Array.AsReadOnly(warnings.ToArray()));
    }

    public static bool IsLikelyVbCableDeviceName(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("VB-CABLE", StringComparison.OrdinalIgnoreCase)
            || friendlyName.Contains("VB-Audio Virtual Cable", StringComparison.OrdinalIgnoreCase)
            || (friendlyName.StartsWith("CABLE", StringComparison.OrdinalIgnoreCase)
                && (friendlyName.Contains(" Input", StringComparison.OrdinalIgnoreCase)
                    || friendlyName.Contains(" Output", StringComparison.OrdinalIgnoreCase)));
    }

    private static string? GetDefaultCaptureDeviceId(
        MMDeviceEnumerator enumerator,
        ICollection<string> warnings)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            {
                return null;
            }

            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            return defaultDevice.ID;
        }
        catch (Exception exception) when (IsRecoverableDeviceException(exception))
        {
            warnings.Add(
                $"Windows did not provide the default capture endpoint: {exception.Message}");
            return null;
        }
    }

    private static AudioEndpoint[] EnumerateActiveEndpoints(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow,
        AudioDeviceDirection direction,
        string? defaultDeviceId,
        ICollection<string> warnings)
    {
        var endpoints = new List<AudioEndpoint>();
        var devices = enumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);

        for (var index = 0; index < devices.Count; index++)
        {
            MMDevice? device = null;

            try
            {
                device = devices[index];

                var deviceId = device.ID;
                var friendlyName = device.FriendlyName;

                endpoints.Add(new AudioEndpoint(
                    deviceId,
                    friendlyName,
                    direction,
                    MapState(device.State),
                    string.Equals(deviceId, defaultDeviceId, StringComparison.Ordinal),
                    IsLikelyVbCableDeviceName(friendlyName)));
            }
            catch (Exception exception) when (IsRecoverableDeviceException(exception))
            {
                warnings.Add(
                    $"Skipped an inaccessible {direction.ToString().ToLowerInvariant()} endpoint "
                    + $"at index {index}: {exception.Message}");
            }
            finally
            {
                device?.Dispose();
            }
        }

        return endpoints
            .OrderBy(endpoint => endpoint.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(endpoint => endpoint.FriendlyName, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.DeviceId, StringComparer.Ordinal)
            .ToArray();
    }

    private static AudioEndpointState MapState(DeviceState state)
    {
        var mappedState = AudioEndpointState.None;

        if (state.HasFlag(DeviceState.Active))
        {
            mappedState |= AudioEndpointState.Active;
        }

        if (state.HasFlag(DeviceState.Disabled))
        {
            mappedState |= AudioEndpointState.Disabled;
        }

        if (state.HasFlag(DeviceState.NotPresent))
        {
            mappedState |= AudioEndpointState.NotPresent;
        }

        if (state.HasFlag(DeviceState.Unplugged))
        {
            mappedState |= AudioEndpointState.Unplugged;
        }

        return mappedState;
    }

    private static bool IsRecoverableDeviceException(Exception exception)
    {
        return exception is COMException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or KeyNotFoundException;
    }
}
