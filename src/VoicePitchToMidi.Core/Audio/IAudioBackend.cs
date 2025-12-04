namespace VoicePitchToMidi.Core.Audio;

public interface IAudioBackend : IDisposable
{
    event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    event EventHandler<string>? Error;

    int SampleRate { get; }
    int BufferSize { get; }
    bool IsRunning { get; }
    string DeviceName { get; }

    void Start();
    void Stop();

    static abstract IReadOnlyList<AudioDeviceInfo> GetDevices();
}

public class AudioDataEventArgs : EventArgs
{
    public required float[] Buffer { get; init; }
    public required int SampleCount { get; init; }
}

public record AudioDeviceInfo(string Id, string Name, int MaxSampleRate, int Channels);
