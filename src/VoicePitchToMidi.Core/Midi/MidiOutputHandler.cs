using NAudio.Midi;

namespace VoicePitchToMidi.Core.Midi;

public sealed class MidiOutputHandler : IDisposable
{
    private readonly MidiOut _midiOut;
    private readonly int _channel;
    private int _currentNote = -1;
    private int _currentVelocity;
    private readonly object _lock = new();
    private bool _disposed;

    public int Channel => _channel;
    public int CurrentNote => _currentNote;
    public bool IsNoteOn => _currentNote >= 0;
    public int CurrentProgram { get; private set; }

    public MidiOutputHandler(int deviceIndex = 0, int channel = 0)
    {
        if (deviceIndex >= MidiOut.NumberOfDevices)
        {
            throw new ArgumentException($"MIDI device index {deviceIndex} not available. Only {MidiOut.NumberOfDevices} devices found.");
        }

        _midiOut = new MidiOut(deviceIndex);
        _channel = Math.Clamp(channel, 0, 15);
    }

    public static IReadOnlyList<MidiDeviceInfo> GetOutputDevices()
    {
        var devices = new List<MidiDeviceInfo>();

        for (int i = 0; i < MidiOut.NumberOfDevices; i++)
        {
            var caps = MidiOut.DeviceInfo(i);
            devices.Add(new MidiDeviceInfo(i, caps.ProductName, caps.Manufacturer.ToString()));
        }

        return devices;
    }

    public void NoteOn(int noteNumber, int velocity = 100)
    {
        if (_disposed || noteNumber is < 0 or > 127) return;
        velocity = Math.Clamp(velocity, 0, 127);

        lock (_lock)
        {
            if (_disposed) return;

            // Turn off current note if different
            if (_currentNote >= 0 && _currentNote != noteNumber)
            {
                SendNoteOffInternal(_currentNote);
            }

            // Don't retrigger same note
            if (_currentNote == noteNumber) return;

            _currentNote = noteNumber;
            _currentVelocity = velocity;

            try
            {
                var noteOnEvent = new NoteOnEvent(0, _channel + 1, noteNumber, velocity, 0);
                _midiOut.Send(noteOnEvent.GetAsShortMessage());
            }
            catch { /* Ignore errors during note on */ }
        }
    }

    public void NoteOff()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;

            if (_currentNote >= 0)
            {
                SendNoteOffInternal(_currentNote);
                _currentNote = -1;
            }
        }
    }

    public void SendPitchBend(float cents)
    {
        if (_disposed) return;

        // Convert cents to pitch bend value
        // Assuming ±2 semitones (200 cents) range (standard)
        // Pitch bend range: 0-16383, center: 8192
        float semitones = cents / 100f;
        float normalized = (semitones / 2f) + 0.5f; // Map -2..+2 semitones to 0..1
        normalized = Math.Clamp(normalized, 0f, 1f);

        int pitchBendValue = (int)(normalized * 16383);

        int lsb = pitchBendValue & 0x7F;
        int msb = (pitchBendValue >> 7) & 0x7F;

        // Pitch bend message: 0xE0 + channel, LSB, MSB
        int message = (0xE0 + _channel) | (lsb << 8) | (msb << 16);

        try
        {
            _midiOut.Send(message);
        }
        catch { /* Ignore errors */ }
    }

    public void ResetPitchBend()
    {
        if (_disposed) return;

        // Center position
        int lsb = 0x00;
        int msb = 0x40; // 8192 >> 7 = 64 = 0x40

        int message = (0xE0 + _channel) | (lsb << 8) | (msb << 16);

        try
        {
            _midiOut.Send(message);
        }
        catch { /* Ignore errors */ }
    }

    /// <summary>
    /// Send a program change message to select an instrument.
    /// </summary>
    /// <param name="program">Program number (0-127). See GeneralMidiProgram enum for standard GM instruments.</param>
    public void SendProgramChange(int program)
    {
        if (_disposed) return;

        program = Math.Clamp(program, 0, 127);
        CurrentProgram = program;

        try
        {
            // Program change: 0xC0 + channel, program
            var programChange = new PatchChangeEvent(0, _channel + 1, program);
            _midiOut.Send(programChange.GetAsShortMessage());
        }
        catch { /* Ignore errors */ }
    }

    /// <summary>
    /// Send a control change message.
    /// </summary>
    public void SendControlChange(int controller, int value)
    {
        if (_disposed) return;

        controller = Math.Clamp(controller, 0, 127);
        value = Math.Clamp(value, 0, 127);

        try
        {
            var ccMessage = new ControlChangeEvent(0, _channel + 1, (MidiController)controller, value);
            _midiOut.Send(ccMessage.GetAsShortMessage());
        }
        catch { /* Ignore errors */ }
    }

    public void AllNotesOff()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                // Send note off for current note
                if (_currentNote >= 0)
                {
                    SendNoteOffInternal(_currentNote);
                    _currentNote = -1;
                }

                // Send All Notes Off CC (123)
                var ccMessage = new ControlChangeEvent(0, _channel + 1, MidiController.AllNotesOff, 0);
                _midiOut.Send(ccMessage.GetAsShortMessage());

                ResetPitchBendInternal();
            }
            catch { /* Ignore errors during cleanup */ }
        }
    }

    private void SendNoteOffInternal(int noteNumber)
    {
        try
        {
            var noteOffEvent = new NoteEvent(0, _channel + 1, MidiCommandCode.NoteOff, noteNumber, 0);
            _midiOut.Send(noteOffEvent.GetAsShortMessage());
        }
        catch { /* Ignore errors */ }
    }

    private void ResetPitchBendInternal()
    {
        try
        {
            int lsb = 0x00;
            int msb = 0x40;
            int message = (0xE0 + _channel) | (lsb << 8) | (msb << 16);
            _midiOut.Send(message);
        }
        catch { /* Ignore errors */ }
    }

    public void Dispose()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;

            try
            {
                // Try to send note off before disposing
                if (_currentNote >= 0)
                {
                    SendNoteOffInternal(_currentNote);
                    _currentNote = -1;
                }
            }
            catch { /* Ignore */ }

            _disposed = true;
        }

        try
        {
            _midiOut.Dispose();
        }
        catch { /* Ignore errors during dispose */ }
    }
}

public record MidiDeviceInfo(int Index, string Name, string Manufacturer);
