using Soundboard.Audio;

namespace Soundboard.App;

internal sealed class AudioServiceLifecycle
{
    private readonly Func<AudioEngineState> getState;
    private readonly Action<string, string, AudioMonitorConfiguration> start;
    private readonly Action stop;

    public AudioServiceLifecycle(AudioMixEngine engine)
        : this(
            () => engine.State,
            engine.Start,
            engine.Stop)
    {
    }

    internal AudioServiceLifecycle(
        Func<AudioEngineState> getState,
        Action<string, string, AudioMonitorConfiguration> start,
        Action stop)
    {
        this.getState = getState;
        this.start = start;
        this.stop = stop;
    }

    public async Task ConnectAsync(
        string microphoneEndpointId,
        string renderEndpointId,
        AudioMonitorConfiguration monitorConfiguration)
    {
        if (getState() != AudioEngineState.Stopped)
        {
            await Task.Run(stop);
        }

        await Task.Run(
            () => start(
                microphoneEndpointId,
                renderEndpointId,
                monitorConfiguration));
    }

    public async Task StopAsync()
    {
        if (getState() != AudioEngineState.Stopped)
        {
            await Task.Run(stop);
        }
    }
}
