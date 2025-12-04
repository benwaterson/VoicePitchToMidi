using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoicePitchToMidi.Core.Audio;

public sealed class WasapiAudioBackend : IAudioBackend
{
    private readonly WasapiCapture _capture;
    private readonly int _targetSampleRate;
    private readonly int _bufferSize;
    private readonly WaveFormat _targetFormat;
    private float[]? _conversionBuffer;
    private bool _isRunning;

    public event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
    public event EventHandler<string>? Error;

    public int SampleRate => _targetSampleRate;
    public int BufferSize => _bufferSize;
    public bool IsRunning => _isRunning;
    public string DeviceName { get; }

    public WasapiAudioBackend(string? deviceId = null, int sampleRate = 44100, int bufferSize = 2048,
        bool exclusiveMode = false)
    {
        _targetSampleRate = sampleRate;
        _bufferSize = bufferSize;
        _targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        var device = GetDevice(deviceId);
        DeviceName = device.FriendlyName;

        var shareMode = exclusiveMode ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
        _capture = new WasapiCapture(device, exclusiveMode, 10); // 10ms latency
        _capture.WaveFormat = _capture.WaveFormat.SampleRate == sampleRate
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1)
            : _capture.WaveFormat;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    private static MMDevice GetDevice(string? deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();

        if (string.IsNullOrEmpty(deviceId))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }

        return enumerator.GetDevice(deviceId);
    }

    public static IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            var format = device.AudioClient.MixFormat;
            devices.Add(new AudioDeviceInfo(
                device.ID,
                device.FriendlyName,
                format.SampleRate,
                format.Channels));
        }

        return devices;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        try
        {
            var sourceFormat = _capture.WaveFormat;
            int sourceSamples = e.BytesRecorded / (sourceFormat.BitsPerSample / 8);
            int sourceChannels = sourceFormat.Channels;
            int monoSamples = sourceSamples / sourceChannels;

            // Ensure buffer is large enough
            if (_conversionBuffer == null || _conversionBuffer.Length < monoSamples)
            {
                _conversionBuffer = new float[monoSamples];
            }

            // Convert to mono float
            ConvertToMonoFloat(e.Buffer, e.BytesRecorded, sourceFormat, _conversionBuffer);

            // Raise event with the audio data
            AudioDataAvailable?.Invoke(this, new AudioDataEventArgs
            {
                Buffer = _conversionBuffer,
                SampleCount = monoSamples
            });
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, $"Audio processing error: {ex.Message}");
        }
    }

    private static void ConvertToMonoFloat(byte[] source, int bytesRecorded, WaveFormat format, float[] destination)
    {
        int channels = format.Channels;
        int bytesPerSample = format.BitsPerSample / 8;
        int totalSamples = bytesRecorded / bytesPerSample;
        int monoIndex = 0;

        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat:
                for (int i = 0; i < totalSamples; i += channels)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        sum += BitConverter.ToSingle(source, (i + ch) * 4);
                    }
                    destination[monoIndex++] = sum / channels;
                }
                break;

            case WaveFormatEncoding.Pcm when bytesPerSample == 2:
                for (int i = 0; i < totalSamples; i += channels)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        short sample = BitConverter.ToInt16(source, (i + ch) * 2);
                        sum += sample / 32768f;
                    }
                    destination[monoIndex++] = sum / channels;
                }
                break;

            case WaveFormatEncoding.Pcm when bytesPerSample == 3:
                for (int i = 0; i < totalSamples; i += channels)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int offset = (i + ch) * 3;
                        int sample = source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16);
                        if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                        sum += sample / 8388608f;
                    }
                    destination[monoIndex++] = sum / channels;
                }
                break;

            case WaveFormatEncoding.Pcm when bytesPerSample == 4:
                for (int i = 0; i < totalSamples; i += channels)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        int sample = BitConverter.ToInt32(source, (i + ch) * 4);
                        sum += sample / 2147483648f;
                    }
                    destination[monoIndex++] = sum / channels;
                }
                break;

            default:
                throw new NotSupportedException($"Unsupported audio format: {format.Encoding}, {bytesPerSample} bytes");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isRunning = false;
        if (e.Exception != null)
        {
            Error?.Invoke(this, $"Recording stopped with error: {e.Exception.Message}");
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _capture.StartRecording();
        _isRunning = true;
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _capture.StopRecording();
        _isRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
    }
}
