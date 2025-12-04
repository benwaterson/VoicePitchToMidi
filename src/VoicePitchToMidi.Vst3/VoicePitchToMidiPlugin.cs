using AudioPlugSharp;
using AudioPlugSharpWPF;
using VoicePitchToMidi.Core;
using VoicePitchToMidi.Core.PitchDetection;

namespace VoicePitchToMidi.Vst3;

/// <summary>
/// VST3 plugin that converts voice pitch to MIDI notes using AudioPlugSharp.
/// </summary>
public class VoicePitchToMidiPlugin : AudioPluginWPF
{
    private PitchToMidiProcessor? _processor;
    private int _lastMidiNote = -1;
    private int _lastVelocity = 0;
    private float _currentConfidence = 0;
    private float _currentFrequency = 0;
    private string _currentNoteName = "---";

    // Parameters
    private AudioPluginParameter? _noiseGateParam;
    private AudioPluginParameter? _minConfidenceParam;
    private AudioPluginParameter? _smoothingParam;
    private AudioPluginParameter? _noteStabilityParam;
    private AudioPluginParameter? _minNoteParam;
    private AudioPluginParameter? _maxNoteParam;
    private AudioPluginParameter? _pitchBendParam;
    private AudioPluginParameter? _velocitySensParam;
    private AudioPluginParameter? _algorithmParam;

    // Audio port
    private DoubleAudioIOPort? _monoInput;

    public VoicePitchToMidiPlugin()
    {
        Company = "VoicePitchToMidi";
        Website = "https://github.com/voicepitchtomidi";
        Contact = "voicepitchtomidi@example.com";
        PluginName = "Voice Pitch to MIDI";
        PluginCategory = "Instrument|Other";
        PluginVersion = "1.0.0";

        // Unique 64bit ID for the plugin
        PluginID = 0x56504D49444901AB; // "VPMIDI" + version

        HasUserInterface = true;
        EditorWidth = 400;
        EditorHeight = 500;

        // WPF requires shared load context
        CacheLoadContext = true;
    }

    public override void Initialize()
    {
        base.Initialize();

        // Input port - mono audio input for voice
        InputPorts = [_monoInput = new DoubleAudioIOPort("Mono Input", EAudioChannelConfiguration.Mono)];
        OutputPorts = []; // No audio output - MIDI only

        // Add parameters
        AddParameter(_noiseGateParam = new AudioPluginParameter
        {
            ID = "noiseGate",
            Name = "Noise Gate",
            MinValue = 0,
            MaxValue = 0.1,
            DefaultValue = 0.01,
            ValueFormat = "{0:F3}"
        });

        AddParameter(_minConfidenceParam = new AudioPluginParameter
        {
            ID = "minConfidence",
            Name = "Min Confidence",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0.8,
            ValueFormat = "{0:P0}"
        });

        AddParameter(_smoothingParam = new AudioPluginParameter
        {
            ID = "smoothing",
            Name = "Smoothing",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 0.3,
            ValueFormat = "{0:P0}"
        });

        AddParameter(_noteStabilityParam = new AudioPluginParameter
        {
            ID = "noteStability",
            Name = "Note Stability",
            MinValue = 1,
            MaxValue = 10,
            DefaultValue = 2,
            ValueFormat = "{0:F0}"
        });

        AddParameter(_minNoteParam = new AudioPluginParameter
        {
            ID = "minNote",
            Name = "Min Note",
            MinValue = 0,
            MaxValue = 127,
            DefaultValue = 36,
            ValueFormat = "{0:F0}"
        });

        AddParameter(_maxNoteParam = new AudioPluginParameter
        {
            ID = "maxNote",
            Name = "Max Note",
            MinValue = 0,
            MaxValue = 127,
            DefaultValue = 84,
            ValueFormat = "{0:F0}"
        });

        AddParameter(_pitchBendParam = new AudioPluginParameter
        {
            ID = "pitchBend",
            Name = "Pitch Bend",
            MinValue = 0,
            MaxValue = 1,
            DefaultValue = 1,
            ValueFormat = "{0:F0}"
        });

        AddParameter(_velocitySensParam = new AudioPluginParameter
        {
            ID = "velocitySensitivity",
            Name = "Velocity Sens",
            MinValue = 0,
            MaxValue = 2,
            DefaultValue = 1,
            ValueFormat = "{0:P0}"
        });

        AddParameter(_algorithmParam = new AudioPluginParameter
        {
            ID = "algorithm",
            Name = "Algorithm",
            MinValue = 0,
            MaxValue = 2,
            DefaultValue = 0,
            ValueFormat = "{0:F0}"
        });
    }

    public override void InitializeProcessing()
    {
        base.InitializeProcessing();

        int sampleRate = (int)Host.SampleRate;
        int bufferSize = (int)Host.MaxAudioBufferSize;

        // Create processor with selected algorithm
        var algorithm = GetSelectedAlgorithm();
        _processor = new PitchToMidiProcessor(sampleRate, Math.Max(bufferSize, 2048), algorithm);

        // Wire up events
        _processor.PitchDetected += OnPitchDetected;
        _processor.MidiNoteChanged += OnMidiNoteChanged;

        ApplySettings();
    }

    public override void Stop()
    {
        base.Stop();

        // Turn off any lingering note
        if (_lastMidiNote >= 0)
        {
            Host.SendNoteOff(0, _lastMidiNote, 0, 0);
            _lastMidiNote = -1;
        }

        if (_processor != null)
        {
            _processor.PitchDetected -= OnPitchDetected;
            _processor.MidiNoteChanged -= OnMidiNoteChanged;
            _processor.Dispose();
            _processor = null;
        }
    }

    private PitchAlgorithm GetSelectedAlgorithm()
    {
        int alg = (int)(_algorithmParam?.ProcessValue ?? 0);
        return alg switch
        {
            1 => PitchAlgorithm.McLeod,
            2 => PitchAlgorithm.Autocorrelation,
            _ => PitchAlgorithm.Yin
        };
    }

    private void ApplySettings()
    {
        if (_processor == null) return;

        _processor.Settings = new ProcessorSettings
        {
            NoiseGate = (float)(_noiseGateParam?.ProcessValue ?? 0.01),
            MinConfidence = (float)(_minConfidenceParam?.ProcessValue ?? 0.8),
            Smoothing = (float)(_smoothingParam?.ProcessValue ?? 0.3),
            NoteStability = (int)(_noteStabilityParam?.ProcessValue ?? 2),
            MinNote = (int)(_minNoteParam?.ProcessValue ?? 36),
            MaxNote = (int)(_maxNoteParam?.ProcessValue ?? 84),
            SendPitchBend = (_pitchBendParam?.ProcessValue ?? 1) > 0.5,
            VelocitySensitivity = (float)(_velocitySensParam?.ProcessValue ?? 1.0)
        };

        // Update algorithm if changed
        var newAlgorithm = GetSelectedAlgorithm();
        if (newAlgorithm != _processor.CurrentAlgorithm)
        {
            _processor.SetAlgorithm(newAlgorithm);
        }
    }

    private void OnPitchDetected(object? sender, PitchEventArgs e)
    {
        _currentFrequency = e.SmoothedFrequency;
        _currentConfidence = e.Confidence;
        _currentNoteName = PitchResult.MidiNoteToName(e.MidiNote);
    }

    private void OnMidiNoteChanged(object? sender, MidiNoteEventArgs e)
    {
        // Note changes are handled in Process() via Host.SendNoteOn/Off
        // Store the info for sending in the audio thread
        _lastMidiNote = e.CurrentNote;
        _lastVelocity = e.Velocity;
    }

    public override void Process()
    {
        base.Process();

        if (_processor == null || _monoInput == null) return;

        // Apply settings each buffer (in case parameters changed)
        ApplySettings();

        // Get input samples
        ReadOnlySpan<double> inSamples = _monoInput.GetAudioBuffer(0);
        int numSamples = inSamples.Length;
        if (numSamples == 0) return;

        // Convert to float array for processor
        float[] buffer = new float[numSamples];
        for (int i = 0; i < numSamples; i++)
        {
            buffer[i] = (float)inSamples[i];
        }

        // Track note state before processing
        int previousNote = _processor.CurrentMidiNote;

        // Process audio
        _processor.ProcessAudioData(buffer, numSamples);

        // Handle MIDI output based on note changes
        int currentNote = _processor.CurrentMidiNote;

        if (currentNote != previousNote)
        {
            // Note changed
            if (previousNote >= 0)
            {
                // Turn off previous note
                Host.SendNoteOff(0, previousNote, 0, 0);
            }

            if (currentNote >= 0)
            {
                // Turn on new note
                int velocity = Math.Clamp(_lastVelocity, 1, 127);
                Host.SendNoteOn(0, currentNote, velocity / 127f, 0);
            }
        }
    }

    public override System.Windows.Controls.UserControl GetEditorView()
    {
        return new PluginEditorView(this);
    }

    // Properties for UI binding
    public float CurrentFrequency => _currentFrequency;
    public float CurrentConfidence => _currentConfidence;
    public string CurrentNoteName => _currentNoteName;
    public int CurrentMidiNote => _processor?.CurrentMidiNote ?? -1;
}
