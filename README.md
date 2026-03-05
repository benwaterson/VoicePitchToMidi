# Voice Pitch to MIDI

Real-time voice pitch detection and MIDI conversion for Windows. Captures audio from a microphone, detects the fundamental frequency, and outputs MIDI note messages. Available as a standalone WPF application and a VST3 plugin.

## Features

- **Real-time pitch detection** with three selectable algorithms
- **MIDI output** with note on/off, pitch bend for micro-tuning, velocity from confidence, and program change for 128 General MIDI instruments
- **Multiple audio backends**: WASAPI (shared and exclusive mode) and ASIO
- **VST3 plugin** for use inside DAWs
- **Configurable parameters**: noise gate, confidence threshold, smoothing, note stability, note range, pitch bend, velocity sensitivity, MIDI channel
- **Dark-themed modern UI** with real-time note display, frequency readout, and confidence meter

## Architecture

```
src/
├── VoicePitchToMidi.Core/           # Core processing library (.NET 10 / .NET 8)
│   ├── Audio/
│   │   ├── IAudioBackend.cs         # Audio backend interface
│   │   ├── WasapiAudioBackend.cs    # WASAPI capture (shared + exclusive)
│   │   └── AsioAudioBackend.cs      # ASIO capture (pro audio)
│   ├── Midi/
│   │   ├── MidiOutputHandler.cs     # Win32 MIDI output (direct winmm.dll)
│   │   ├── VirtualMidiPort.cs       # Internal MIDI queue for VST3
│   │   └── GeneralMidiProgram.cs    # 128 GM instrument definitions
│   ├── PitchDetection/
│   │   ├── IPitchDetector.cs        # Detector interface + PitchResult
│   │   ├── PitchAlgorithm.cs        # Algorithm enum + factory
│   │   ├── YinPitchDetector.cs      # YIN algorithm (AVX2 optimized)
│   │   ├── McLeodPitchDetector.cs   # McLeod Pitch Method (MPM/NSDF)
│   │   └── AutocorrelationPitchDetector.cs
│   └── PitchToMidiProcessor.cs      # Main processing pipeline
│
├── VoicePitchToMidi.Standalone/     # WPF standalone application (.NET 10)
│   ├── MainWindow.xaml              # UI layout
│   ├── MainViewModel.cs             # MVVM view model
│   ├── AppSettings.cs               # JSON settings persistence
│   └── App.xaml                     # Dark theme resources
│
└── VoicePitchToMidi.Vst3/           # VST3 plugin (.NET 8)
    ├── VoicePitchToMidiPlugin.cs    # AudioPlugSharp plugin
    └── PluginEditorView.xaml        # Plugin UI
```

## Processing Pipeline

```
Microphone → Audio Backend (WASAPI/ASIO)
    → Ring Buffer (8192 samples, circular)
    → Overlapping Windows (2048 samples, 512-sample hop = 75% overlap)
    → Noise Gate (RMS threshold)
    → Pitch Detector (YIN / McLeod / Autocorrelation)
    → Confidence Filter
    → Frequency Smoothing (exponential moving average)
    → MIDI Note Mapping (69 + 12 * log2(f / 440))
    → Note Range Filter
    → Hysteresis (candidate note tracking, N consecutive frames)
    → MIDI Output (Note On/Off, Pitch Bend, Program Change)
```

## Pitch Detection Algorithms

### YIN (default)
Based on "YIN, a fundamental frequency estimator for speech and music" by de Cheveigné and Kawahara. Calculates the difference function, applies cumulative mean normalization, finds the first dip below threshold, and refines with parabolic interpolation. AVX2 SIMD-accelerated when available. Best accuracy for voice, ~25ms latency.

### McLeod Pitch Method (MPM)
Based on "A Smarter Way to Find Pitch" by McLeod and Wyvill. Uses the Normalized Square Difference Function (NSDF), finds positive region peaks, selects the first peak exceeding a cutoff ratio of the highest peak. Good balance of speed and accuracy.

### Autocorrelation
Simple autocorrelation with peak finding after the first minimum. Fastest but least accurate. Suitable for low-latency scenarios where some pitch error is acceptable.

## Audio Backends

| Backend | Latency | Notes |
|---------|---------|-------|
| **WASAPI** | ~20-30ms | Shared mode, works with any device, Windows handles resampling |
| **WASAPI (Exclusive)** | ~5ms | Bypasses Windows audio engine, uses device's native format |
| **ASIO** | ~2-5ms | Lowest latency, requires ASIO driver, tries 48k/44.1k/96k/88.2k/192k |

## Parameters

| Parameter | Default | Range | Description |
|-----------|---------|-------|-------------|
| Noise Gate | 0.01 | 0.0 - 0.1 | RMS threshold below which audio is treated as silence |
| Min Confidence | 50% | 0% - 100% | Reject pitch detections below this confidence level |
| Smoothing | 60% | 0% - 100% | Exponential smoothing factor (higher = more responsive) |
| Note Stability | 3 | 1 - 10 | Consecutive frames of same note required before triggering |
| Min Note | C2 (36) | 0 - 127 | Lowest MIDI note to accept |
| Max Note | C6 (84) | 0 - 127 | Highest MIDI note to accept |
| Pitch Bend | On | On/Off | Send pitch bend messages for micro-tuning within semitones |
| Velocity Sensitivity | 100% | 0% - 200% | Maps detection confidence to MIDI velocity |
| MIDI Channel | 1 | 1 - 16 | MIDI output channel |

## MIDI Output

Uses direct Win32 `winmm.dll` P/Invoke (`midiOutOpen` with `CALLBACK_NULL`) for maximum compatibility across .NET versions. Sends:

- **Note On/Off** when detected pitch changes (with hysteresis to prevent jitter)
- **Pitch Bend** (±2 semitones) for continuous micro-tuning between semitones
- **Program Change** for instrument selection (128 General MIDI programs)
- **All Notes Off** (CC 123) on silence or stop

## VST3 Plugin

The VST3 plugin (`VoicePitchToMidi.Vst3`) uses the AudioPlugSharp framework. It accepts mono audio input, runs pitch detection, and outputs MIDI note events to the host DAW. All parameters are exposed as automatable plugin parameters. The plugin UI is a compact WPF view (420x520px) with real-time feedback.

**Plugin ID:** `0x56504D49444901AB`
**Category:** Instrument|Other

## Requirements

- **OS:** Windows 10/11 (x64)
- **Runtime:** .NET 10 (standalone), .NET 8 (VST3)
- **SDK:** .NET 10.0.100-preview.4 or later (for building)
- **Audio:** Any WASAPI or ASIO-compatible input device
- **MIDI:** Any Windows MIDI output device, or a virtual MIDI loopback driver (e.g., loopMIDI) for routing to DAWs

## Building

```bash
dotnet build
```

To publish a self-contained executable:

```bash
dotnet publish src/VoicePitchToMidi.Standalone -c Release -r win-x64
```

## Dependencies

- [NAudio](https://github.com/naudio/NAudio) 2.2.1 - Audio I/O (WASAPI, ASIO)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4.0 - MVVM framework
- [AudioPlugSharp](https://github.com/miinstruments/AudioPlugSharp) 0.7.9 - VST3 plugin framework

## Settings Persistence

Settings are saved as JSON to `%APPDATA%/VoicePitchToMidi/settings.json` and restored on startup. All parameter changes are applied immediately while running.

## Tips

- For **lowest latency** with a standard mic/headset, use WASAPI (Exclusive)
- For **professional audio interfaces** with native ASIO drivers, use the ASIO backend
- ASIO4ALL can have issues when input and output devices run at different sample rates - WASAPI avoids this
- If your MIDI device is in use by a DAW, install [loopMIDI](https://www.tobias-erichsen.de/software/loopmidi.html) to create a virtual MIDI port
- Lower **Note Stability** for faster response, raise it to reduce false note triggers
- Lower **Min Confidence** to detect quieter/breathier singing, raise it to reject noise
