using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Soundboard.Audio;

public sealed class AudioDeviceService : IDisposable, IMMNotificationClient
{
    private readonly MMDeviceEnumerator notificationEnumerator = new();
    private bool disposed;

    public AudioDeviceService()
    {
        notificationEnumerator.RegisterEndpointNotificationCallback(this);
    }

    public event EventHandler<AudioDeviceChangedEventArgs>? DevicesChanged;

    public AudioDeviceSnapshot GetActiveDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var warnings = new List<string>();
        var defaultCaptureDeviceId = GetDefaultCaptureDeviceId(enumerator, warnings);
        var defaultRenderDeviceId = GetDefaultRenderDeviceId(enumerator, warnings);

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
            defaultRenderDeviceId,
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

        return ContainsVbCableDriverIdentity(friendlyName);
    }

    public static bool IsLikelyVbCableDevice(
        string? friendlyName,
        string? interfaceFriendlyName,
        string? endpointDescription,
        string? interfacePath)
    {
        return ContainsVbCableDriverIdentity(interfaceFriendlyName)
            || ContainsVbCableDriverIdentity(interfacePath)
            || ContainsVbCableDriverIdentity(friendlyName)
            || ContainsVbCableDriverIdentity(endpointDescription);
    }

    internal static bool IsLikelyVbCableDevice(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsLikelyVbCableDevice(
            device.FriendlyName,
            GetPropertyString(
                device,
                PropertyKeys.PKEY_DeviceInterface_FriendlyName),
            GetPropertyString(device, PropertyKeys.PKEY_Device_DeviceDesc),
            GetPropertyString(device, PropertyKeys.PKEY_Device_InterfaceKey));
    }

    internal static string? GetControllerDeviceId(MMDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return GetPropertyString(
            device,
            PropertyKeys.PKEY_Device_ControllerDeviceId);
    }

    public AudioFormatInfo GetEndpointMixFormat(
        string endpointId,
        AudioDeviceDirection expectedDirection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDevice(endpointId);
        var expectedDataFlow = expectedDirection == AudioDeviceDirection.Capture
            ? DataFlow.Capture
            : DataFlow.Render;

        if (device.DataFlow != expectedDataFlow)
        {
            throw new InvalidOperationException(
                $"Endpoint \"{device.FriendlyName}\" is not a "
                + $"{expectedDirection.ToString().ToLowerInvariant()} "
                + "endpoint.");
        }

        return AudioFormatInfo.FromWaveFormat(device.AudioClient.MixFormat);
    }

    private static string? GetDefaultCaptureDeviceId(
        MMDeviceEnumerator enumerator,
        ICollection<string> warnings)
    {
        return GetDefaultDeviceId(
            enumerator,
            DataFlow.Capture,
            "communications capture",
            Role.Communications,
            warnings);
    }

    private static string? GetDefaultRenderDeviceId(
        MMDeviceEnumerator enumerator,
        ICollection<string> warnings)
    {
        return GetDefaultDeviceId(
            enumerator,
            DataFlow.Render,
            "render",
            Role.Console,
            warnings);
    }

    private static string? GetDefaultDeviceId(
        MMDeviceEnumerator enumerator,
        DataFlow dataFlow,
        string directionLabel,
        Role role,
        ICollection<string> warnings)
    {
        try
        {
            if (!enumerator.HasDefaultAudioEndpoint(dataFlow, role))
            {
                return null;
            }

            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(
                dataFlow,
                role);
            return defaultDevice.ID;
        }
        catch (Exception exception) when (IsRecoverableDeviceException(exception))
        {
            warnings.Add(
                $"Windows did not provide the default {directionLabel} endpoint: "
                + exception.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        notificationEnumerator.UnregisterEndpointNotificationCallback(this);
        notificationEnumerator.Dispose();
    }

    void IMMNotificationClient.OnDeviceStateChanged(
        string deviceId,
        DeviceState newState) =>
        RaiseDeviceChanged(
            new AudioDeviceChangedEventArgs(
                AudioDeviceChangeKind.StateChanged,
                deviceId,
                null,
                null,
                newState));

    void IMMNotificationClient.OnDeviceAdded(string deviceId) =>
        RaiseDeviceChanged(new AudioDeviceChangedEventArgs(
            AudioDeviceChangeKind.Added,
            deviceId));

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) =>
        RaiseDeviceChanged(new AudioDeviceChangedEventArgs(
            AudioDeviceChangeKind.Removed,
            deviceId));

    void IMMNotificationClient.OnDefaultDeviceChanged(
        DataFlow flow,
        Role role,
        string defaultDeviceId) =>
        RaiseDeviceChanged(new AudioDeviceChangedEventArgs(
            AudioDeviceChangeKind.DefaultChanged,
            defaultDeviceId,
            flow,
            role));

    void IMMNotificationClient.OnPropertyValueChanged(
        string deviceId,
        PropertyKey propertyKey) =>
        RaiseDeviceChanged(new AudioDeviceChangedEventArgs(
            AudioDeviceChangeKind.PropertyChanged,
            deviceId));

    private void RaiseDeviceChanged(AudioDeviceChangedEventArgs eventArgs)
    {
        // Core Audio requires notification callbacks to remain nonblocking.
        ThreadPool.QueueUserWorkItem(
            _ => DevicesChanged?.Invoke(this, eventArgs));
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
                var interfaceFriendlyName = GetPropertyString(
                    device,
                    PropertyKeys.PKEY_DeviceInterface_FriendlyName);
                var endpointDescription = GetPropertyString(
                    device,
                    PropertyKeys.PKEY_Device_DeviceDesc);
                var controllerDeviceId = GetControllerDeviceId(device);
                var interfacePath = GetPropertyString(
                    device,
                    PropertyKeys.PKEY_Device_InterfaceKey);

                endpoints.Add(new AudioEndpoint(
                    deviceId,
                    friendlyName,
                    direction,
                    MapState(device.State),
                    string.Equals(deviceId, defaultDeviceId, StringComparison.Ordinal),
                    IsLikelyVbCableDevice(
                        friendlyName,
                        interfaceFriendlyName,
                        endpointDescription,
                        interfacePath),
                    interfaceFriendlyName,
                    endpointDescription,
                    controllerDeviceId,
                    interfacePath));
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

    private static bool ContainsVbCableDriverIdentity(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && (value.Contains(
                    "VB-Audio Virtual Cable",
                    StringComparison.OrdinalIgnoreCase)
                || value.Contains(
                    "VBAudioVAC",
                    StringComparison.OrdinalIgnoreCase)
                || value.Contains(
                    "VB-CABLE",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetPropertyString(
        MMDevice device,
        PropertyKey propertyKey)
    {
        try
        {
            if (!device.Properties.Contains(propertyKey))
            {
                return null;
            }

            return device.Properties[propertyKey].Value?.ToString();
        }
        catch (Exception exception) when (IsRecoverableDeviceException(exception))
        {
            return null;
        }
    }
}
