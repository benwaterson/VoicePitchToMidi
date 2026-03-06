using System.Threading;
using VoicePitchToMidi.Core.Audio;
using VoicePitchToMidi.Core.Midi;
using VoicePitchToMidi.Core.PitchDetection;

namespace VoicePitchToMidi.Core;

/// <summary>
/// Main processor that connects audio input, pitch detection, and MIDI output.
/// Audio callback writes to a lock-free SPSC buffer; a dedicated background thread
/// runs pitch detection and onset analysis without blocking the audio path.
/// </summary>
public sealed class PitchToMidiProcessor : IDisposable
{
    private IPitchDetector _pitchDetector;
    private readonly SpscRingBuffer _audioBuffer;
    private readonly float[] _analysisBuffer;
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly int _hopSize;

    // Dedicated processing thread
    private Thread? _processingThread;
    private readonly ManualResetEventSlim _dataReady = new(false);
    private volatile bool _running;

    // Onset detection
    private readonly OnsetDetector _onsetDetector = new();
    private readonly float[] _onsetBuffer = new float[256];
    private bool _pendingOnset;
    private int _onsetAge; // hops since onset was detected
    private float _onsetRms; // RMS at onset time, used for velocity
    private const int OnsetExpiryHops = 3;
    private const int OnsetSmoothingHops = 4; // hops of reduced smoothing after onset

    private MidiOutputHandler? _midiOutput;
    private VirtualMidiPort? _virtualMidiPort;

    private int _lastMidiNote = -1;
    private int _candidateNote = -1;
    private int _noteHoldCounter;
    private int _silenceCounter;
    private float _smoothedFrequency;
    private bool _disposed;
    private PitchAlgorithm _currentAlgorithm = PitchAlgorithm.Yin;

    // Spectrum visualization
    private readonly float[] _spectrumBins = new float[129]; // HalfFft + 1
    private readonly float[] _zeroBins = new float[129];
    private long _lastSpectrumTicks;

    public event EventHandler<PitchEventArgs>? PitchDetected;
    public event EventHandler<MidiNoteEventArgs>? MidiNoteChanged;
    public event EventHandler<SpectrumEventArgs>? SpectrumUpdated;

    // Configuration — read by processing thread, written by UI thread.
    // ProcessorSettings is a class; the reference swap is atomic on .NET.
    public ProcessorSettings Settings { get; set; } = new();

    public bool IsProcessing => _running;
    public float CurrentFrequency => _smoothedFrequency;
    public int CurrentMidiNote => _lastMidiNote;
    public PitchAlgorithm CurrentAlgorithm => _currentAlgorithm;

    public PitchToMidiProcessor(int sampleRate = 44100, int bufferSize = 2048,
        PitchAlgorithm algorithm = PitchAlgorithm.Yin, int hopSize = 512)
    {
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
        _hopSize = hopSize;
        _currentAlgorithm = algorithm;

        _pitchDetector = PitchDetectorFactory.Create(algorithm, sampleRate, bufferSize);
        _audioBuffer = new SpscRingBuffer(bufferSize * 4);
        _analysisBuffer = new float[bufferSize];
    }

    /// <summary>
    /// Start the background processing thread.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _processingThread = new Thread(ProcessingLoop)
        {
            Name = "PitchProcessor",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _processingThread.Start();
    }

    /// <summary>
    /// Stop the background processing thread and wait for it to exit.
    /// </summary>
    public void Stop()
    {
        _running = false;
        _dataReady.Set(); // wake the thread so it can exit
        _processingThread?.Join(timeout: TimeSpan.FromSeconds(2));
        _processingThread = null;
    }

    /// <summary>
    /// Switch to a different pitch detection algorithm.
    /// Uses Interlocked.Exchange for atomic reference swap.
    /// </summary>
    public void SetAlgorithm(PitchAlgorithm algorithm)
    {
        if (algorithm == _currentAlgorithm) return;

        _currentAlgorithm = algorithm;
        var newDetector = PitchDetectorFactory.Create(algorithm, _sampleRate, _bufferSize);
        Interlocked.Exchange(ref _pitchDetector, newDetector);
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

    /// <summary>
    /// Called from the audio callback thread. Writes samples to the lock-free buffer
    /// and signals the processing thread. Returns in microseconds.
    /// </summary>
    public void ProcessAudioData(float[] buffer, int sampleCount)
    {
        if (_disposed) return;

        _audioBuffer.Write(buffer.AsSpan(0, sampleCount));
        _dataReady.Set();
    }

    /// <summary>
    /// Background processing loop. Waits for audio data, then runs onset detection
    /// and pitch analysis in overlapping windows.
    /// </summary>
    private void ProcessingLoop()
    {
        while (_running)
        {
            _dataReady.Wait();
            _dataReady.Reset();

            if (!_running) break;

            var settings = Settings; // snapshot reference once per wake-up
            int windowSize = settings.BufferSize;

            while (_audioBuffer.Available >= windowSize && _running)
            {
                _audioBuffer.Peek(_analysisBuffer.AsSpan(0, windowSize));
                _audioBuffer.Advance(_hopSize);

                var analysisSpan = _analysisBuffer.AsSpan(0, windowSize);

                // RMS noise gate
                float rms = CalculateRms(analysisSpan);
                if (rms < settings.NoiseGate)
                {
                    RaiseSpectrumIfDue(true);
                    HandleSilence(settings);
                    continue;
                }

                // 1. Onset detection on last 256 samples of the window
                int onsetStart = Math.Max(0, windowSize - 256);
                int onsetLen = Math.Min(256, windowSize);
                analysisSpan.Slice(onsetStart, onsetLen).CopyTo(_onsetBuffer.AsSpan(0, onsetLen));

                float onsetStrength = _onsetDetector.Detect(
                    _onsetBuffer.AsSpan(0, onsetLen), out float detectedRms);

                RaiseSpectrumIfDue(false);

                if (onsetStrength > 0 && !_pendingOnset)
                {
                    _pendingOnset = true;
                    _onsetAge = 0;
                    _onsetRms = detectedRms;
                }

                if (_pendingOnset)
                    _onsetAge++;

                // 2. Pitch detection on full window
                var detector = _pitchDetector; // read reference once
                var result = detector.DetectPitch(analysisSpan);

                // 3. Two-phase note triggering
                if (result.HasPitch && result.Confidence >= settings.MinConfidence)
                {
                    if (_pendingOnset && _onsetAge <= OnsetExpiryHops)
                    {
                        // Onset + pitch: trigger immediately, skip hysteresis
                        ProcessPitchResultImmediate(result, settings);
                        _pendingOnset = false;
                    }
                    else
                    {
                        // Normal tonal path with smoothing + hysteresis
                        ProcessPitchResult(result, settings);
                    }
                }
                else
                {
                    // No pitch detected
                    if (_pendingOnset && _onsetAge <= OnsetExpiryHops)
                    {
                        // Transient still decaying — wait for pitch to resolve
                    }
                    else
                    {
                        if (_pendingOnset)
                            _pendingOnset = false; // expired false alarm

                        HandleSilence(settings);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Immediate onset-triggered note: bypasses hysteresis, uses onset RMS for velocity.
    /// </summary>
    private void ProcessPitchResultImmediate(PitchResult result, ProcessorSettings settings)
    {
        _silenceCounter = 0;

        // Adaptive smoothing: during onset, use minimal smoothing for fast tracking
        _smoothedFrequency = result.Frequency;

        int midiNote = FrequencyToMidiNote(_smoothedFrequency);
        if (midiNote < settings.MinNote || midiNote > settings.MaxNote)
        {
            HandleSilence(settings);
            return;
        }

        PitchDetected?.Invoke(this, new PitchEventArgs(
            result.Frequency, _smoothedFrequency, result.Confidence, midiNote));

        if (midiNote != _lastMidiNote)
        {
            // Use onset RMS for velocity instead of confidence
            float velocityBase = Math.Max(_onsetRms * 8f, result.Confidence);
            TriggerNoteChange(midiNote, velocityBase, settings);
        }

        _noteHoldCounter = 0;
        _candidateNote = -1;
    }

    private void ProcessPitchResult(PitchResult result, ProcessorSettings settings)
    {
        _silenceCounter = 0;

        // Adaptive smoothing: reduced smoothing shortly after onset
        float smoothingFactor = settings.Smoothing;
        if (_onsetAge > 0 && _onsetAge <= OnsetSmoothingHops)
        {
            // Blend toward 1.0 (no smoothing) during onset settling
            float onsetWeight = 1f - (_onsetAge / (float)OnsetSmoothingHops);
            smoothingFactor = smoothingFactor + (1f - smoothingFactor) * onsetWeight;
        }

        // Smooth frequency
        if (_smoothedFrequency <= 0)
        {
            _smoothedFrequency = result.Frequency;
        }
        else
        {
            _smoothedFrequency = _smoothedFrequency * (1 - smoothingFactor) + result.Frequency * smoothingFactor;
        }

        // Get MIDI note
        int midiNote = FrequencyToMidiNote(_smoothedFrequency);

        // Apply note range filter
        if (midiNote < settings.MinNote || midiNote > settings.MaxNote)
        {
            HandleSilence(settings);
            return;
        }

        // Raise pitch detected event
        PitchDetected?.Invoke(this, new PitchEventArgs(result.Frequency, _smoothedFrequency, result.Confidence, midiNote));

        // Handle note changes with hysteresis
        if (midiNote != _lastMidiNote)
        {
            if (midiNote != _candidateNote)
            {
                _candidateNote = midiNote;
                _noteHoldCounter = 1;
            }
            else
            {
                _noteHoldCounter++;
            }

            if (_noteHoldCounter >= settings.NoteStability)
            {
                TriggerNoteChange(midiNote, result.Confidence, settings);
                _noteHoldCounter = 0;
                _candidateNote = -1;
            }
        }
        else
        {
            _noteHoldCounter = 0;
            _candidateNote = -1;

            // Send pitch bend for micro-tuning if enabled
            if (settings.SendPitchBend && _lastMidiNote >= 0)
            {
                float exactMidiNote = (float)(69.0 + 12.0 * Math.Log2(_smoothedFrequency / 440.0));
                float cents = (exactMidiNote - _lastMidiNote) * 100f;
                SendPitchBend(cents, settings);
            }
        }
    }

    private void TriggerNoteChange(int newNote, float confidence, ProcessorSettings settings)
    {
        int oldNote = _lastMidiNote;
        _lastMidiNote = newNote;

        int velocity = (int)(confidence * 127 * settings.VelocitySensitivity);
        velocity = Math.Clamp(velocity, 1, 127);

        // Send MIDI
        _midiOutput?.NoteOn(newNote, velocity);
        _virtualMidiPort?.SendNoteOn(settings.MidiChannel, newNote, velocity);

        MidiNoteChanged?.Invoke(this, new MidiNoteEventArgs(oldNote, newNote, velocity));
    }

    private void SendPitchBend(float cents, ProcessorSettings settings)
    {
        _midiOutput?.SendPitchBend(cents);

        if (_virtualMidiPort != null)
        {
            float semitones = cents / 100f;
            float normalized = (semitones / 2f) + 0.5f;
            normalized = Math.Clamp(normalized, 0f, 1f);
            int pitchBendValue = (int)(normalized * 16383);
            _virtualMidiPort.SendPitchBend(settings.MidiChannel, pitchBendValue);
        }
    }

    private void HandleSilence(ProcessorSettings settings)
    {
        _silenceCounter++;
        _noteHoldCounter = 0;

        int threshold = settings.PercussiveMode ? 5 : settings.SilenceThreshold;

        if (_silenceCounter >= threshold && _lastMidiNote >= 0)
        {
            _midiOutput?.NoteOff();
            _midiOutput?.ResetPitchBend();
            _virtualMidiPort?.AllNotesOff(settings.MidiChannel);

            int oldNote = _lastMidiNote;
            _lastMidiNote = -1;
            _candidateNote = -1;
            _smoothedFrequency = 0;

            MidiNoteChanged?.Invoke(this, new MidiNoteEventArgs(oldNote, -1, 0));
        }
    }

    private void RaiseSpectrumIfDue(bool silent)
    {
        if (SpectrumUpdated == null) return;

        long now = Environment.TickCount64;
        if (now - _lastSpectrumTicks < 33) return; // ~30 fps
        _lastSpectrumTicks = now;

        if (silent)
        {
            SpectrumUpdated.Invoke(this, new SpectrumEventArgs(_zeroBins));
        }
        else
        {
            _onsetDetector.CopyMagnitudesTo(_spectrumBins);
            SpectrumUpdated.Invoke(this, new SpectrumEventArgs((float[])_spectrumBins.Clone()));
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
        _audioBuffer.Clear();
        _pitchDetector.Reset();
        _onsetDetector.Reset();
        _lastMidiNote = -1;
        _candidateNote = -1;
        _noteHoldCounter = 0;
        _silenceCounter = 0;
        _smoothedFrequency = 0;
        _pendingOnset = false;
        _onsetAge = 0;

        _midiOutput?.AllNotesOff();
        _virtualMidiPort?.AllNotesOff(Settings.MidiChannel);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        Reset();
        _dataReady.Dispose();
        _midiOutput?.Dispose();
        _virtualMidiPort?.Dispose();
    }
}

public class ProcessorSettings
{
    public int BufferSize { get; set; } = 2048;
    public float NoiseGate { get; set; } = 0.01f;
    public float MinConfidence { get; set; } = 0.5f;
    public float Smoothing { get; set; } = 0.6f;
    public int NoteStability { get; set; } = 3;
    public int SilenceThreshold { get; set; } = 20;
    public int MinNote { get; set; } = 36;  // C2
    public int MaxNote { get; set; } = 84;  // C6
    public bool SendPitchBend { get; set; } = true;
    public float VelocitySensitivity { get; set; } = 1.0f;
    public int MidiChannel { get; set; } = 0;
    public bool PercussiveMode { get; set; }
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

public class SpectrumEventArgs : EventArgs
{
    public float[] Bins { get; }

    public SpectrumEventArgs(float[] bins)
    {
        Bins = bins;
    }
}
