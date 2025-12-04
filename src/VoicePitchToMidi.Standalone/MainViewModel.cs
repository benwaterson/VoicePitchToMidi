using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoicePitchToMidi.Core;
using VoicePitchToMidi.Core.Audio;
using VoicePitchToMidi.Core.Midi;
using VoicePitchToMidi.Core.PitchDetection;

namespace VoicePitchToMidi.Standalone;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private PitchToMidiProcessor? _processor;
    private IAudioBackend? _audioBackend;
    private MidiOutputHandler? _midiOutput;
    private bool _isRunning;
    private bool _disposed;
    private bool _isLoadingSettings;

    // Device Lists
    [ObservableProperty]
    private ObservableCollection<string> _audioBackends = ["WASAPI", "ASIO"];

    [ObservableProperty]
    private string _selectedAudioBackend = "WASAPI";

    [ObservableProperty]
    private ObservableCollection<AudioDeviceInfo> _audioDevices = [];

    [ObservableProperty]
    private AudioDeviceInfo? _selectedAudioDevice;

    [ObservableProperty]
    private ObservableCollection<MidiDeviceInfo> _midiDevices = [];

    [ObservableProperty]
    private MidiDeviceInfo? _selectedMidiDevice;

    // Algorithm selection
    [ObservableProperty]
    private ObservableCollection<AlgorithmOption> _algorithms =
    [
        new AlgorithmOption(PitchAlgorithm.Yin, "YIN", "Most accurate for voice, ~25ms latency"),
        new AlgorithmOption(PitchAlgorithm.McLeod, "McLeod (MPM)", "Good balance of speed and accuracy"),
        new AlgorithmOption(PitchAlgorithm.Autocorrelation, "Autocorrelation", "Fastest, lower accuracy")
    ];

    [ObservableProperty]
    private AlgorithmOption? _selectedAlgorithm;

    // Instrument selection
    [ObservableProperty]
    private ObservableCollection<InstrumentOption> _instruments = [];

    [ObservableProperty]
    private InstrumentOption? _selectedInstrument;

    // Display Properties
    [ObservableProperty]
    private string _currentNoteName = "---";

    [ObservableProperty]
    private float _currentFrequency;

    [ObservableProperty]
    private string _currentMidiNote = "---";

    [ObservableProperty]
    private float _confidence;

    [ObservableProperty]
    private double _confidenceWidth;

    [ObservableProperty]
    private string _startStopButtonText = "Start";

    // Settings
    [ObservableProperty]
    private float _noiseGate = 0.01f;

    [ObservableProperty]
    private float _minConfidence = 0.8f;

    [ObservableProperty]
    private float _smoothing = 0.3f;

    [ObservableProperty]
    private int _noteStability = 2;

    [ObservableProperty]
    private int _minNote = 36;

    [ObservableProperty]
    private int _maxNote = 84;

    [ObservableProperty]
    private bool _sendPitchBend = true;

    [ObservableProperty]
    private float _velocitySensitivity = 1.0f;

    [ObservableProperty]
    private int _midiChannel = 1;

    public string MinNoteName => PitchResult.MidiNoteToName(MinNote);
    public string MaxNoteName => PitchResult.MidiNoteToName(MaxNote);

    public MainViewModel()
    {
        // Prevent saving during initialization
        _isLoadingSettings = true;

        // Initialize instruments
        foreach (var (number, name) in GeneralMidiHelper.GetAllPrograms())
        {
            Instruments.Add(new InstrumentOption(number, name));
        }

        SelectedAlgorithm = Algorithms[0];
        SelectedInstrument = Instruments[0];

        RefreshDevices();

        _isLoadingSettings = false;
        LoadSettings();
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;
        try
        {
            var settings = AppSettings.Load();

            // Audio backend
            if (AudioBackends.Contains(settings.AudioBackend))
            {
                SelectedAudioBackend = settings.AudioBackend;
            }

            // Audio device - refresh first to ensure we have the list
            RefreshAudioDevices();
            if (!string.IsNullOrEmpty(settings.AudioDeviceId))
            {
                var device = AudioDevices.FirstOrDefault(d => d.Id == settings.AudioDeviceId);
                if (device != null)
                {
                    SelectedAudioDevice = device;
                }
            }

            // MIDI device
            if (!string.IsNullOrEmpty(settings.MidiDeviceName))
            {
                var device = MidiDevices.FirstOrDefault(d => d.Name == settings.MidiDeviceName);
                if (device != null)
                {
                    SelectedMidiDevice = device;
                }
            }

            // Algorithm
            var algorithm = Algorithms.FirstOrDefault(a => a.Algorithm == settings.Algorithm);
            if (algorithm != null)
            {
                SelectedAlgorithm = algorithm;
            }

            // Instrument
            if (settings.InstrumentProgram >= 0 && settings.InstrumentProgram < Instruments.Count)
            {
                SelectedInstrument = Instruments[settings.InstrumentProgram];
            }

            // Processing settings
            NoiseGate = settings.NoiseGate;
            MinConfidence = settings.MinConfidence;
            Smoothing = settings.Smoothing;
            NoteStability = settings.NoteStability;
            MinNote = settings.MinNote;
            MaxNote = settings.MaxNote;
            SendPitchBend = settings.SendPitchBend;
            VelocitySensitivity = settings.VelocitySensitivity;
            MidiChannel = settings.MidiChannel;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public void SaveSettings()
    {
        if (_isLoadingSettings) return;

        var settings = new AppSettings
        {
            AudioBackend = SelectedAudioBackend,
            AudioDeviceId = SelectedAudioDevice?.Id,
            MidiDeviceName = SelectedMidiDevice?.Name,
            Algorithm = SelectedAlgorithm?.Algorithm ?? PitchAlgorithm.Yin,
            InstrumentProgram = SelectedInstrument?.Program ?? 0,
            NoiseGate = NoiseGate,
            MinConfidence = MinConfidence,
            Smoothing = Smoothing,
            NoteStability = NoteStability,
            MinNote = MinNote,
            MaxNote = MaxNote,
            SendPitchBend = SendPitchBend,
            VelocitySensitivity = VelocitySensitivity,
            MidiChannel = MidiChannel
        };

        settings.Save();
    }

    partial void OnSelectedAudioBackendChanged(string value)
    {
        RefreshAudioDevices();
        SaveSettings();
    }

    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value) => SaveSettings();
    partial void OnSelectedMidiDeviceChanged(MidiDeviceInfo? value) => SaveSettings();
    partial void OnMinNoteChanged(int value) { OnPropertyChanged(nameof(MinNoteName)); SaveSettings(); }
    partial void OnMaxNoteChanged(int value) { OnPropertyChanged(nameof(MaxNoteName)); SaveSettings(); }
    partial void OnNoiseGateChanged(float value) => SaveSettings();
    partial void OnMinConfidenceChanged(float value) => SaveSettings();
    partial void OnSmoothingChanged(float value) => SaveSettings();
    partial void OnNoteStabilityChanged(int value) => SaveSettings();
    partial void OnSendPitchBendChanged(bool value) => SaveSettings();
    partial void OnVelocitySensitivityChanged(float value) => SaveSettings();
    partial void OnMidiChannelChanged(int value) => SaveSettings();

    partial void OnSelectedAlgorithmChanged(AlgorithmOption? value)
    {
        if (value != null && _processor != null)
        {
            _processor.SetAlgorithm(value.Algorithm);
        }
        SaveSettings();
    }

    partial void OnSelectedInstrumentChanged(InstrumentOption? value)
    {
        if (value != null && _midiOutput != null)
        {
            _midiOutput.SendProgramChange(value.Program);
        }
        SaveSettings();
    }

    private void RefreshDevices()
    {
        RefreshAudioDevices();
        RefreshMidiDevices();
    }

    private void RefreshAudioDevices()
    {
        AudioDevices.Clear();

        try
        {
            var devices = SelectedAudioBackend == "WASAPI"
                ? WasapiAudioBackend.GetDevices()
                : AsioAudioBackend.GetDevices();

            foreach (var device in devices)
            {
                AudioDevices.Add(device);
            }

            if (AudioDevices.Count > 0 && SelectedAudioDevice == null)
            {
                SelectedAudioDevice = AudioDevices[0];
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading audio devices: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshMidiDevices()
    {
        MidiDevices.Clear();

        try
        {
            foreach (var device in MidiOutputHandler.GetOutputDevices())
            {
                MidiDevices.Add(device);
            }

            if (MidiDevices.Count > 0 && SelectedMidiDevice == null)
            {
                SelectedMidiDevice = MidiDevices[0];
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading MIDI devices: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void StartStop()
    {
        if (_isRunning)
        {
            Stop();
        }
        else
        {
            Start();
        }
    }

    private void Start()
    {
        try
        {
            // Create audio backend
            if (SelectedAudioDevice == null)
            {
                MessageBox.Show("Please select an audio device.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int sampleRate = 44100;
            int bufferSize = 2048;

            if (SelectedAudioBackend == "WASAPI")
            {
                _audioBackend = new WasapiAudioBackend(SelectedAudioDevice.Id, sampleRate, bufferSize);
            }
            else
            {
                _audioBackend = new AsioAudioBackend(SelectedAudioDevice.Id, 0, bufferSize);
                sampleRate = _audioBackend.SampleRate;
            }

            // Create processor with selected algorithm
            var algorithm = SelectedAlgorithm?.Algorithm ?? PitchAlgorithm.Yin;
            _processor = new PitchToMidiProcessor(sampleRate, bufferSize, algorithm);
            ApplySettings();

            // Create MIDI output
            if (SelectedMidiDevice != null)
            {
                _midiOutput = new MidiOutputHandler(SelectedMidiDevice.Index, MidiChannel - 1);
                _processor.SetMidiOutput(_midiOutput);

                // Send program change for selected instrument
                if (SelectedInstrument != null)
                {
                    _midiOutput.SendProgramChange(SelectedInstrument.Program);
                }
            }

            // Wire up events
            _audioBackend.AudioDataAvailable += OnAudioDataAvailable;
            _audioBackend.Error += OnAudioError;
            _processor.PitchDetected += OnPitchDetected;
            _processor.MidiNoteChanged += OnMidiNoteChanged;

            // Start
            _audioBackend.Start();
            _isRunning = true;
            StartStopButtonText = "Stop";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error starting: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Stop();
        }
    }

    private void Stop()
    {
        // Disconnect events first
        if (_audioBackend != null)
        {
            _audioBackend.AudioDataAvailable -= OnAudioDataAvailable;
            _audioBackend.Error -= OnAudioError;
        }

        if (_processor != null)
        {
            _processor.PitchDetected -= OnPitchDetected;
            _processor.MidiNoteChanged -= OnMidiNoteChanged;
        }

        // Stop and dispose in order
        _audioBackend?.Stop();

        // Dispose MIDI first (while processor might still reference it)
        _midiOutput?.Dispose();
        _midiOutput = null;

        _processor?.Dispose();
        _processor = null;

        _audioBackend?.Dispose();
        _audioBackend = null;

        _isRunning = false;
        StartStopButtonText = "Start";

        CurrentNoteName = "---";
        CurrentMidiNote = "---";
        CurrentFrequency = 0;
        Confidence = 0;
        ConfidenceWidth = 0;
    }

    [RelayCommand]
    private void Reset()
    {
        _processor?.Reset();
        CurrentNoteName = "---";
        CurrentMidiNote = "---";
        CurrentFrequency = 0;
        Confidence = 0;
        ConfidenceWidth = 0;
    }

    private void ApplySettings()
    {
        if (_processor == null) return;

        _processor.Settings = new ProcessorSettings
        {
            NoiseGate = NoiseGate,
            MinConfidence = MinConfidence,
            Smoothing = Smoothing,
            NoteStability = NoteStability,
            MinNote = MinNote,
            MaxNote = MaxNote,
            SendPitchBend = SendPitchBend,
            VelocitySensitivity = VelocitySensitivity,
            MidiChannel = MidiChannel - 1
        };
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        _processor?.ProcessAudioData(e.Buffer, e.SampleCount);
    }

    private void OnAudioError(object? sender, string error)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            MessageBox.Show($"Audio error: {error}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }

    private void OnPitchDetected(object? sender, PitchEventArgs e)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CurrentFrequency = e.SmoothedFrequency;
            CurrentNoteName = PitchResult.MidiNoteToName(e.MidiNote);
            CurrentMidiNote = e.MidiNote.ToString();
            Confidence = e.Confidence;
            ConfidenceWidth = e.Confidence * 400; // Assuming ~400px max width
        });
    }

    private void OnMidiNoteChanged(object? sender, MidiNoteEventArgs e)
    {
        if (e.CurrentNote < 0)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentNoteName = "---";
                CurrentMidiNote = "---";
            });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveSettings();
        Stop();
    }
}

public record AlgorithmOption(PitchAlgorithm Algorithm, string Name, string Description)
{
    public override string ToString() => Name;
}

public record InstrumentOption(int Program, string Name)
{
    public override string ToString() => $"{Program}: {Name}";
}
