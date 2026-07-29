namespace Soundboard.Audio;

[Flags]
public enum AudioEndpointState
{
    None = 0,
    Active = 1,
    Disabled = 2,
    NotPresent = 4,
    Unplugged = 8
}
