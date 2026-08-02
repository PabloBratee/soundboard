using Soundboard.Audio;

namespace Soundboard.App;

internal static class AudioEndpointSelectionPolicy
{
    public static IReadOnlyList<AudioEndpoint> PhysicalMicrophones(
        IEnumerable<AudioEndpoint> endpoints) =>
        endpoints
            .Where(
                endpoint => endpoint.Direction == AudioDeviceDirection.Capture
                    && endpoint.State.HasFlag(AudioEndpointState.Active)
                    && !endpoint.IsLikelyVbCable)
            .ToArray();

    public static IReadOnlyList<AudioEndpoint> VirtualOutputs(
        IEnumerable<AudioEndpoint> endpoints) =>
        endpoints
            .Where(
                endpoint => endpoint.Direction == AudioDeviceDirection.Render
                    && endpoint.State.HasFlag(AudioEndpointState.Active)
                    && endpoint.IsLikelyVbCable)
            .ToArray();

    public static AudioEndpoint? SelectMicrophone(
        IReadOnlyList<AudioEndpoint> physicalMicrophones,
        bool useWindowsDefault,
        string? pinnedEndpointId)
    {
        if (!useWindowsDefault && !string.IsNullOrWhiteSpace(pinnedEndpointId))
        {
            var pinned = physicalMicrophones.FirstOrDefault(
                endpoint => string.Equals(
                    endpoint.DeviceId,
                    pinnedEndpointId,
                    StringComparison.Ordinal));
            if (pinned is not null)
            {
                return pinned;
            }
        }

        return physicalMicrophones.FirstOrDefault(endpoint => endpoint.IsDefault)
            ?? physicalMicrophones.FirstOrDefault();
    }

    public static AudioEndpoint? SelectVirtualOutput(
        IReadOnlyList<AudioEndpoint> virtualOutputs,
        string? configuredEndpointId)
    {
        if (!string.IsNullOrWhiteSpace(configuredEndpointId))
        {
            return virtualOutputs.FirstOrDefault(
                endpoint => string.Equals(
                    endpoint.DeviceId,
                    configuredEndpointId,
                    StringComparison.Ordinal));
        }

        return virtualOutputs.FirstOrDefault(
                endpoint => endpoint.EndpointDescription?.Contains(
                    "CABLE Input",
                    StringComparison.OrdinalIgnoreCase) == true)
            ?? virtualOutputs.FirstOrDefault(
                endpoint => endpoint.FriendlyName.Contains(
                    "CABLE Input",
                    StringComparison.OrdinalIgnoreCase))
            ?? virtualOutputs.FirstOrDefault();
    }

    public static string? UpdateConfiguredEndpointId(
        string? configuredEndpointId,
        AudioEndpoint? selectedEndpoint,
        bool userInitiated) =>
        userInitiated
            ? selectedEndpoint?.DeviceId
            : configuredEndpointId;
}
