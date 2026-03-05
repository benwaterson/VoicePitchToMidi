using System.Runtime.InteropServices;

namespace VoicePitchToMidi.Core.Midi;

public sealed class MidiOutputHandler : IDisposable
{
    private nint _handle;
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
        int deviceCount = WinMM.midiOutGetNumDevs();
        if (deviceIndex >= deviceCount)
        {
            throw new ArgumentException($"MIDI device index {deviceIndex} not available. Only {deviceCount} devices found.");
        }

        // Open with CALLBACK_NULL (0) - no callback needed for output
        int result = WinMM.midiOutOpen(out _handle, deviceIndex, IntPtr.Zero, IntPtr.Zero, 0);
        if (result != 0)
        {
            throw new InvalidOperationException($"midiOutOpen failed with error code {result} (device {deviceIndex})");
        }

        _channel = Math.Clamp(channel, 0, 15);
    }

    public static IReadOnlyList<MidiDeviceInfo> GetOutputDevices()
    {
        var devices = new List<MidiDeviceInfo>();
        int count = WinMM.midiOutGetNumDevs();

        for (int i = 0; i < count; i++)
        {
            var caps = new WinMM.MIDIOUTCAPS();
            if (WinMM.midiOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<WinMM.MIDIOUTCAPS>()) == 0)
            {
                devices.Add(new MidiDeviceInfo(i, caps.szPname, $"MID={caps.wMid}"));
            }
        }

        return devices;
    }

    private void SendShort(int message)
    {
        if (_disposed || _handle == 0) return;
        WinMM.midiOutShortMsg(_handle, message);
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

            // Note On: 0x90 + channel | note << 8 | velocity << 16
            SendShort((0x90 + _channel) | (noteNumber << 8) | (velocity << 16));
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
        SendShort((0xE0 + _channel) | (lsb << 8) | (msb << 16));
    }

    public void ResetPitchBend()
    {
        if (_disposed) return;

        // Center position: 8192 = 0x00 LSB, 0x40 MSB
        SendShort((0xE0 + _channel) | (0x00 << 8) | (0x40 << 16));
    }

    /// <summary>
    /// Send a program change message to select an instrument.
    /// </summary>
    public void SendProgramChange(int program)
    {
        if (_disposed) return;

        program = Math.Clamp(program, 0, 127);
        CurrentProgram = program;

        // Program change: 0xC0 + channel | program << 8
        SendShort((0xC0 + _channel) | (program << 8));
    }

    /// <summary>
    /// Send a control change message.
    /// </summary>
    public void SendControlChange(int controller, int value)
    {
        if (_disposed) return;

        controller = Math.Clamp(controller, 0, 127);
        value = Math.Clamp(value, 0, 127);

        // CC: 0xB0 + channel | controller << 8 | value << 16
        SendShort((0xB0 + _channel) | (controller << 8) | (value << 16));
    }

    public void AllNotesOff()
    {
        if (_disposed) return;

        lock (_lock)
        {
            if (_disposed) return;

            // Send note off for current note
            if (_currentNote >= 0)
            {
                SendNoteOffInternal(_currentNote);
                _currentNote = -1;
            }

            // Send All Notes Off CC (123)
            SendShort((0xB0 + _channel) | (123 << 8) | (0 << 16));

            // Reset pitch bend
            SendShort((0xE0 + _channel) | (0x00 << 8) | (0x40 << 16));
        }
    }

    private void SendNoteOffInternal(int noteNumber)
    {
        // Note Off: 0x80 + channel | note << 8 | 0 velocity << 16
        SendShort((0x80 + _channel) | (noteNumber << 8));
    }

    public void Dispose()
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

            _disposed = true;
        }

        if (_handle != 0)
        {
            WinMM.midiOutReset(_handle);
            WinMM.midiOutClose(_handle);
            _handle = 0;
        }
    }
}

public record MidiDeviceInfo(int Index, string Name, string Manufacturer);

/// <summary>
/// Direct Win32 MIDI output P/Invoke - avoids NAudio's CALLBACK_FUNCTION
/// marshaling issues on newer .NET runtimes.
/// </summary>
internal static partial class WinMM
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MIDIOUTCAPS
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

    [LibraryImport("winmm.dll")]
    internal static partial int midiOutGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Auto)]
    internal static extern int midiOutGetDevCaps(IntPtr uDeviceID, ref MIDIOUTCAPS lpMidiOutCaps, int cbMidiOutCaps);

    [LibraryImport("winmm.dll")]
    internal static partial int midiOutOpen(out nint lphmo, int uDeviceID, IntPtr dwCallback, IntPtr dwCallbackInstance, int dwFlags);

    [LibraryImport("winmm.dll")]
    internal static partial int midiOutClose(nint hmo);

    [LibraryImport("winmm.dll")]
    internal static partial int midiOutShortMsg(nint hmo, int dwMsg);

    [LibraryImport("winmm.dll")]
    internal static partial int midiOutReset(nint hmo);
}
