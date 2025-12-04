namespace VoicePitchToMidi.Core.PitchDetection;

public interface IPitchDetector
{
    PitchResult DetectPitch(ReadOnlySpan<float> audioBuffer);
    void Reset();
}

public readonly record struct PitchResult(float Frequency, float Confidence, bool HasPitch)
{
    public static PitchResult NoResult => new(0f, 0f, false);

    public int MidiNote => HasPitch ? FrequencyToMidiNote(Frequency) : -1;

    public float MidiNoteCents => HasPitch ? GetCentsDeviation() : 0f;

    private static int FrequencyToMidiNote(float frequency)
    {
        // MIDI note = 69 + 12 * log2(frequency / 440)
        return (int)Math.Round(69 + 12 * Math.Log2(frequency / 440.0));
    }

    private float GetCentsDeviation()
    {
        int midiNote = MidiNote;
        float exactMidiNote = 69f + 12f * (float)Math.Log2(Frequency / 440.0);
        return (exactMidiNote - midiNote) * 100f;
    }

    public static float MidiNoteToFrequency(int midiNote)
    {
        return 440f * (float)Math.Pow(2, (midiNote - 69) / 12.0);
    }

    public static string MidiNoteToName(int midiNote)
    {
        if (midiNote < 0 || midiNote > 127)
            return "---";

        string[] noteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
        int octave = (midiNote / 12) - 1;
        int noteIndex = midiNote % 12;
        return $"{noteNames[noteIndex]}{octave}";
    }
}
