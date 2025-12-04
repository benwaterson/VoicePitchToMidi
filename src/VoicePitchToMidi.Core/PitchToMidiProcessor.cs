using VoicePitchToMidi.Core.Audio;
using VoicePitchToMidi.Core.Midi;
using VoicePitchToMidi.Core.PitchDetection;

namespace VoicePitchToMidi.Core;

/// <summary>
/// Main processor that connects audio input, pitch detection, and MIDI output.
/// </summary>
public sealed class PitchToMidiProcessor : IDisposable
{
    private IPitchDetector _pitchDetector;
    private readonly RingBuffer _audioBuffer;
    private readonly object _lock = new();
    private readonly float[] _analysisBuffer;
    private readonly int _sampleRate;
    private readonly int _bufferSize;

    private MidiOutputHandler? _midiOutput;
    private VirtualMidiPort? _virtualMidiPort;

    private int _lastMidiNote = -1;
    private int _noteHoldCounter;
    private int _silenceCounter;
    private float _smoothedFrequency;
    private bool _disposed;
    private PitchAlgorithm _currentAlgorithm = PitchAlgorithm.Yin;

    public event EventHandler<PitchEventArgs>? PitchDetected;
    public event EventHandler<MidiNoteEventArgs>? MidiNoteChanged;

    // Configuration
    public ProcessorSettings Settings { get; set; } = new();

    public bool IsProcessing { get; private set; }
    public float CurrentFrequency => _smoothedFrequency;
    public int CurrentMidiNote => _lastMidiNote;
    public PitchAlgorithm CurrentAlgorithm => _currentAlgorithm;

    public PitchToMidiProcessor(int sampleRate = 44100, int bufferSize = 2048,
        PitchAlgorithm algorithm = PitchAlgorithm.Yin)
    {
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
        _currentAlgorithm = algorithm;

        _pitchDetector = PitchDetectorFactory.Create(algorithm, sampleRate, bufferSize);
        _audioBuffer = new RingBuffer(bufferSize * 4);
        _analysisBuffer = new float[bufferSize];
    }

    /// <summary>
    /// Switch to a different pitch detection algorithm.
    /// </summary>
    public void SetAlgorithm(PitchAlgorithm algorithm)
    {
        if (algorithm == _currentAlgorithm) return;

        lock (_lock)
        {
            _currentAlgorithm = algorithm;
            _pitchDetector = PitchDetectorFactory.Create(algorithm, _sampleRate, _bufferSize);
        }
    }

    /// <summary>
    /// Send a MIDI program change to select an instrument.
    /// </summary>
    public void SetInstrument(int program)
    {
        _midiOutput?.SendProgramChange(program);
    }

    /// <summary>
    /// Send a MIDI program change to select an instrument.
    /// </summary>
    public void SetInstrument(GeneralMidiProgram program)
    {
        SetInstrument((int)program);
    }

    public void SetMidiOutput(MidiOutputHandler midiOutput)
    {
        _midiOutput?.Dispose();
        _midiOutput = midiOutput;
    }

    public void SetVirtualMidiPort(VirtualMidiPort port)
    {
        _virtualMidiPort?.Dispose();
        _virtualMidiPort = port;
    }

    public void ProcessAudioData(float[] buffer, int sampleCount)
    {
        if (_disposed) return;

        lock (_lock)
        {
            _audioBuffer.Write(buffer.AsSpan(0, sampleCount));

            // Process when we have enough samples
            int requiredSamples = Settings.BufferSize;
            while (_audioBuffer.Available >= requiredSamples)
            {
                _audioBuffer.Read(_analysisBuffer.AsSpan(0, requiredSamples));
                var analysisBuffer = _analysisBuffer.AsSpan(0, requiredSamples);

                // Check for silence (gate)
                float rms = CalculateRms(analysisBuffer);
                if (rms < Settings.NoiseGate)
                {
                    HandleSilence();
                    continue;
                }

                // Detect pitch
                var result = _pitchDetector.DetectPitch(analysisBuffer);

                if (result.HasPitch && result.Confidence >= Settings.MinConfidence)
                {
                    ProcessPitchResult(result);
                }
                else
                {
                    HandleSilence();
                }
            }
        }
    }

    private void ProcessPitchResult(PitchResult result)
    {
        _silenceCounter = 0;

        // Smooth frequency
        if (_smoothedFrequency <= 0)
        {
            _smoothedFrequency = result.Frequency;
        }
        else
        {
            _smoothedFrequency = _smoothedFrequency * (1 - Settings.Smoothing) + result.Frequency * Settings.Smoothing;
        }

        // Get MIDI note
        int midiNote = FrequencyToMidiNote(_smoothedFrequency);

        // Apply note range filter
        if (midiNote < Settings.MinNote || midiNote > Settings.MaxNote)
        {
            HandleSilence();
            return;
        }

        // Raise pitch detected event
        PitchDetected?.Invoke(this, new PitchEventArgs(result.Frequency, _smoothedFrequency, result.Confidence, midiNote));

        // Handle note changes with hysteresis
        if (midiNote != _lastMidiNote)
        {
            _noteHoldCounter++;

            if (_noteHoldCounter >= Settings.NoteStability)
            {
                TriggerNoteChange(midiNote, result.Confidence);
                _noteHoldCounter = 0;
            }
        }
        else
        {
            _noteHoldCounter = 0;

            // Send pitch bend for micro-tuning if enabled
            if (Settings.SendPitchBend && _lastMidiNote >= 0)
            {
                float cents = result.MidiNoteCents;
                SendPitchBend(cents);
            }
        }
    }

    private void TriggerNoteChange(int newNote, float confidence)
    {
        int oldNote = _lastMidiNote;
        _lastMidiNote = newNote;

        int velocity = (int)(confidence * 127 * Settings.VelocitySensitivity);
        velocity = Math.Clamp(velocity, 1, 127);

        // Send MIDI
        _midiOutput?.NoteOn(newNote, velocity);
        _virtualMidiPort?.SendNoteOn(Settings.MidiChannel, newNote, velocity);

        MidiNoteChanged?.Invoke(this, new MidiNoteEventArgs(oldNote, newNote, velocity));
    }

    private void SendPitchBend(float cents)
    {
        _midiOutput?.SendPitchBend(cents);

        if (_virtualMidiPort != null)
        {
            float semitones = cents / 100f;
            float normalized = (semitones / 2f) + 0.5f;
            normalized = Math.Clamp(normalized, 0f, 1f);
            int pitchBendValue = (int)(normalized * 16383);
            _virtualMidiPort.SendPitchBend(Settings.MidiChannel, pitchBendValue);
        }
    }

    private void HandleSilence()
    {
        _silenceCounter++;
        _noteHoldCounter = 0;

        if (_silenceCounter >= Settings.SilenceThreshold && _lastMidiNote >= 0)
        {
            _midiOutput?.NoteOff();
            _midiOutput?.ResetPitchBend();
            _virtualMidiPort?.AllNotesOff(Settings.MidiChannel);

            int oldNote = _lastMidiNote;
            _lastMidiNote = -1;
            _smoothedFrequency = 0;

            MidiNoteChanged?.Invoke(this, new MidiNoteEventArgs(oldNote, -1, 0));
        }
    }

    private static float CalculateRms(ReadOnlySpan<float> buffer)
    {
        float sum = 0;
        foreach (float sample in buffer)
        {
            sum += sample * sample;
        }
        return MathF.Sqrt(sum / buffer.Length);
    }

    private static int FrequencyToMidiNote(float frequency)
    {
        return (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));
    }

    public void Reset()
    {
        lock (_lock)
        {
            _audioBuffer.Clear();
            _pitchDetector.Reset();
            _lastMidiNote = -1;
            _noteHoldCounter = 0;
            _silenceCounter = 0;
            _smoothedFrequency = 0;

            _midiOutput?.AllNotesOff();
            _virtualMidiPort?.AllNotesOff(Settings.MidiChannel);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Reset();
        _midiOutput?.Dispose();
        _virtualMidiPort?.Dispose();
    }
}

public class ProcessorSettings
{
    public int BufferSize { get; set; } = 2048;
    public float NoiseGate { get; set; } = 0.01f;
    public float MinConfidence { get; set; } = 0.8f;
    public float Smoothing { get; set; } = 0.3f;
    public int NoteStability { get; set; } = 2;
    public int SilenceThreshold { get; set; } = 5;
    public int MinNote { get; set; } = 36;  // C2
    public int MaxNote { get; set; } = 84;  // C6
    public bool SendPitchBend { get; set; } = true;
    public float VelocitySensitivity { get; set; } = 1.0f;
    public int MidiChannel { get; set; } = 0;
}

public class PitchEventArgs : EventArgs
{
    public float RawFrequency { get; }
    public float SmoothedFrequency { get; }
    public float Confidence { get; }
    public int MidiNote { get; }

    public PitchEventArgs(float rawFrequency, float smoothedFrequency, float confidence, int midiNote)
    {
        RawFrequency = rawFrequency;
        SmoothedFrequency = smoothedFrequency;
        Confidence = confidence;
        MidiNote = midiNote;
    }
}

public class MidiNoteEventArgs : EventArgs
{
    public int PreviousNote { get; }
    public int CurrentNote { get; }
    public int Velocity { get; }

    public MidiNoteEventArgs(int previousNote, int currentNote, int velocity)
    {
        PreviousNote = previousNote;
        CurrentNote = currentNote;
        Velocity = velocity;
    }
}

internal class RingBuffer
{
    private readonly float[] _buffer;
    private int _writePos;
    private int _readPos;
    private int _available;
    private readonly object _lock = new();

    public int Available
    {
        get { lock (_lock) return _available; }
    }

    public RingBuffer(int capacity)
    {
        _buffer = new float[capacity];
    }

    public void Write(ReadOnlySpan<float> data)
    {
        lock (_lock)
        {
            foreach (float sample in data)
            {
                _buffer[_writePos] = sample;
                _writePos = (_writePos + 1) % _buffer.Length;

                if (_available < _buffer.Length)
                    _available++;
                else
                    _readPos = (_readPos + 1) % _buffer.Length; // Overwrite oldest
            }
        }
    }

    public void Read(Span<float> destination)
    {
        lock (_lock)
        {
            int toRead = Math.Min(destination.Length, _available);
            for (int i = 0; i < toRead; i++)
            {
                destination[i] = _buffer[_readPos];
                _readPos = (_readPos + 1) % _buffer.Length;
            }
            _available -= toRead;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _writePos = 0;
            _readPos = 0;
            _available = 0;
        }
    }
}
