using System.Runtime.InteropServices;

namespace VoicePitchToMidi.Core.Midi;

/// <summary>
/// Creates a virtual MIDI port for internal routing (e.g., for VST3 plugin).
/// Uses Windows Multimedia MIDI or can interface with loopMIDI if installed.
/// </summary>
public sealed class VirtualMidiPort : IDisposable
{
    private readonly Queue<MidiMessage> _messageQueue = new();
    private readonly object _lock = new();
    private int _currentNote = -1;

    public event EventHandler<MidiMessage>? MessageReceived;

    public string PortName { get; }
    public bool IsOpen { get; private set; }

    public VirtualMidiPort(string portName = "VoicePitchToMidi")
    {
        PortName = portName;
        IsOpen = true;
    }

    public void SendNoteOn(int channel, int noteNumber, int velocity)
    {
        if (noteNumber is < 0 or > 127) return;

        lock (_lock)
        {
            // Turn off current note if different
            if (_currentNote >= 0 && _currentNote != noteNumber)
            {
                var offMsg = new MidiMessage(MidiMessageType.NoteOff, channel, _currentNote, 0);
                EnqueueMessage(offMsg);
            }

            if (_currentNote != noteNumber)
            {
                _currentNote = noteNumber;
                var msg = new MidiMessage(MidiMessageType.NoteOn, channel, noteNumber, velocity);
                EnqueueMessage(msg);
            }
        }
    }

    public void SendNoteOff(int channel, int noteNumber)
    {
        lock (_lock)
        {
            if (_currentNote == noteNumber)
            {
                _currentNote = -1;
                var msg = new MidiMessage(MidiMessageType.NoteOff, channel, noteNumber, 0);
                EnqueueMessage(msg);
            }
        }
    }

    public void SendPitchBend(int channel, int value)
    {
        var msg = new MidiMessage(MidiMessageType.PitchBend, channel, value & 0x7F, (value >> 7) & 0x7F);
        EnqueueMessage(msg);
    }

    public void AllNotesOff(int channel)
    {
        lock (_lock)
        {
            if (_currentNote >= 0)
            {
                var msg = new MidiMessage(MidiMessageType.NoteOff, channel, _currentNote, 0);
                EnqueueMessage(msg);
                _currentNote = -1;
            }

            // CC 123 - All Notes Off
            var ccMsg = new MidiMessage(MidiMessageType.ControlChange, channel, 123, 0);
            EnqueueMessage(ccMsg);
        }
    }

    private void EnqueueMessage(MidiMessage message)
    {
        lock (_lock)
        {
            _messageQueue.Enqueue(message);
        }
        MessageReceived?.Invoke(this, message);
    }

    public bool TryDequeue(out MidiMessage message)
    {
        lock (_lock)
        {
            if (_messageQueue.Count > 0)
            {
                message = _messageQueue.Dequeue();
                return true;
            }
            message = default;
            return false;
        }
    }

    public IEnumerable<MidiMessage> DequeueAll()
    {
        lock (_lock)
        {
            while (_messageQueue.Count > 0)
            {
                yield return _messageQueue.Dequeue();
            }
        }
    }

    public void Dispose()
    {
        IsOpen = false;
        lock (_lock)
        {
            _messageQueue.Clear();
        }
    }
}

public enum MidiMessageType : byte
{
    NoteOff = 0x80,
    NoteOn = 0x90,
    PolyAftertouch = 0xA0,
    ControlChange = 0xB0,
    ProgramChange = 0xC0,
    ChannelAftertouch = 0xD0,
    PitchBend = 0xE0
}

public readonly record struct MidiMessage(MidiMessageType Type, int Channel, int Data1, int Data2)
{
    public byte[] ToBytes()
    {
        byte status = (byte)((byte)Type | (Channel & 0x0F));
        return Type switch
        {
            MidiMessageType.ProgramChange or MidiMessageType.ChannelAftertouch
                => [status, (byte)(Data1 & 0x7F)],
            _ => [status, (byte)(Data1 & 0x7F), (byte)(Data2 & 0x7F)]
        };
    }

    public int ToInt32()
    {
        byte status = (byte)((byte)Type | (Channel & 0x0F));
        return status | ((Data1 & 0x7F) << 8) | ((Data2 & 0x7F) << 16);
    }
}
