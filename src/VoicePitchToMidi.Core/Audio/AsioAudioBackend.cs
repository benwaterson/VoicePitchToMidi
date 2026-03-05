using NAudio.Wave;

namespace VoicePitchToMidi.Core.Audio;

public sealed class AsioAudioBackend : IAudioBackend
{
    private static readonly int[] CommonSampleRates = [48000, 44100, 96000, 88200, 192000];

    private readonly AsioOut _asioOut;
    private readonly int _bufferSize;
    private readonly int _inputChannel;
    private float[]? _monoBuffer;
    private float[]? _interleavedBuffer;
    private bool _isRunning;
    private bool _initialized;

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<string>? Error;

    public int SampleRate { get; private set; }
    public int BufferSize => _bufferSize;
    public bool IsRunning => _isRunning;
    public string DeviceName { get; }

    public AsioAudioBackend(string driverName, int inputChannel = 0, int bufferSize = 2048)
    {
        _bufferSize = bufferSize;
        _inputChannel = inputChannel;
        DeviceName = driverName;

        _asioOut = new AsioOut(driverName);

        if (_asioOut.DriverInputChannelCount <= 0)
            throw new InvalidOperationException("No ASIO input channels available");

        // Try common sample rates to find one the driver supports
        InitWithSupportedRate();

        _asioOut.AudioAvailable += OnAudioAvailable;
        _asioOut.DriverResetRequest += OnDriverResetRequest;
    }

    private void InitWithSupportedRate()
    {
        foreach (int rate in CommonSampleRates)
        {
            try
            {
                var silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2));
                _asioOut.Init(silence);
                SampleRate = _asioOut.OutputWaveFormat.SampleRate;
                _asioOut.Stop();
                _initialized = true;
                return;
            }
            catch
            {
                // This rate not supported, try next
            }
        }

        throw new InvalidOperationException(
            $"ASIO driver '{DeviceName}' did not accept any common sample rate ({string.Join(", ", CommonSampleRates)} Hz). " +
            "Open the ASIO control panel and check your driver settings.");
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
                    0, // actual rate determined at Init time
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

    /// <summary>
    /// Show the ASIO driver's native control panel.
    /// </summary>
    public void ShowControlPanel()
    {
        _asioOut.ShowControlPanel();
    }

    public void Start()
    {
        if (_isRunning) return;

        if (!_initialized)
            InitWithSupportedRate();

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
