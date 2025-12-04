using NAudio.Wave;

namespace VoicePitchToMidi.Core.Audio;

public sealed class AsioAudioBackend : IAudioBackend
{
    private readonly AsioOut _asioOut;
    private readonly int _bufferSize;
    private readonly int _inputChannel;
    private float[]? _monoBuffer;
    private float[]? _interleavedBuffer;
    private bool _isRunning;

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<string>? Error;

    public int SampleRate { get; }
    public int BufferSize => _bufferSize;
    public bool IsRunning => _isRunning;
    public string DeviceName { get; }

    public AsioAudioBackend(string driverName, int inputChannel = 0, int bufferSize = 2048)
    {
        _bufferSize = bufferSize;
        _inputChannel = inputChannel;
        DeviceName = driverName;

        _asioOut = new AsioOut(driverName);
        SampleRate = _asioOut.DriverInputChannelCount > 0
            ? 44100 // ASIO drivers typically support this
            : throw new InvalidOperationException("No ASIO input channels available");

        _asioOut.AudioAvailable += OnAudioAvailable;
        _asioOut.DriverResetRequest += OnDriverResetRequest;
    }

    public static IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        foreach (var driverName in AsioOut.GetDriverNames())
        {
            try
            {
                using var asio = new AsioOut(driverName);
                devices.Add(new AudioDeviceInfo(
                    driverName,
                    driverName,
                    44100, // Standard ASIO sample rate
                    asio.DriverInputChannelCount));
            }
            catch
            {
                // Skip drivers that can't be initialized
            }
        }

        return devices;
    }

    private void OnAudioAvailable(object? sender, AsioAudioAvailableEventArgs e)
    {
        try
        {
            int samplesPerChannel = e.SamplesPerBuffer;

            if (_monoBuffer == null || _monoBuffer.Length < samplesPerChannel)
            {
                _monoBuffer = new float[samplesPerChannel];
            }

            // Get samples from selected input channel
            int inputChannels = e.InputBuffers.Length;
            int totalSamples = samplesPerChannel * inputChannels;
            if (_interleavedBuffer == null || _interleavedBuffer.Length < totalSamples)
            {
                _interleavedBuffer = new float[totalSamples];
            }
            e.GetAsInterleavedSamples(_interleavedBuffer);
            var inputBuffer = _interleavedBuffer;

            if (_inputChannel < inputChannels)
            {
                for (int i = 0; i < samplesPerChannel; i++)
                {
                    _monoBuffer[i] = inputBuffer[i * inputChannels + _inputChannel];
                }
            }

            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs
            {
                Buffer = _monoBuffer,
                SampleCount = samplesPerChannel
            });
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"ASIO audio processing error: {ex.Message}");
        }
    }

    private void OnDriverResetRequest(object? sender, EventArgs e)
    {
        Error?.Invoke(this, "ASIO driver reset requested - restarting...");
        Stop();
        Start();
    }

    public void Start()
    {
        if (_isRunning) return;

        // Create a dummy wave provider to initialize ASIO
        var silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
        _asioOut.Init(silence);
        _asioOut.Play();
        _isRunning = true;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _asioOut.Stop();
        _isRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _asioOut.AudioAvailable -= OnAudioAvailable;
        _asioOut.Dispose();
    }
}

internal class SilenceProvider : IWaveProvider
{
    public WaveFormat WaveFormat { get; }

    public SilenceProvider(WaveFormat waveFormat)
    {
        WaveFormat = waveFormat;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
