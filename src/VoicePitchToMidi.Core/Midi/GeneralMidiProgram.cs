namespace VoicePitchToMidi.Core.Midi;

/// <summary>
/// General MIDI program numbers (0-127).
/// Standard instrument sounds supported by all GM-compatible synthesizers.
/// </summary>
public enum GeneralMidiProgram
{
    // Piano (0-7)
    AcousticGrandPiano = 0,
    BrightAcousticPiano = 1,
    ElectricGrandPiano = 2,
    HonkyTonkPiano = 3,
    ElectricPiano1 = 4,
    ElectricPiano2 = 5,
    Harpsichord = 6,
    Clavinet = 7,

    // Chromatic Percussion (8-15)
    Celesta = 8,
    Glockenspiel = 9,
    MusicBox = 10,
    Vibraphone = 11,
    Marimba = 12,
    Xylophone = 13,
    TubularBells = 14,
    Dulcimer = 15,

    // Organ (16-23)
    DrawbarOrgan = 16,
    PercussiveOrgan = 17,
    RockOrgan = 18,
    ChurchOrgan = 19,
    ReedOrgan = 20,
    Accordion = 21,
    Harmonica = 22,
    TangoAccordion = 23,

    // Guitar (24-31)
    AcousticGuitarNylon = 24,
    AcousticGuitarSteel = 25,
    ElectricGuitarJazz = 26,
    ElectricGuitarClean = 27,
    ElectricGuitarMuted = 28,
    OverdrivenGuitar = 29,
    DistortionGuitar = 30,
    GuitarHarmonics = 31,

    // Bass (32-39)
    AcousticBass = 32,
    ElectricBassFinger = 33,
    ElectricBassPick = 34,
    FretlessBass = 35,
    SlapBass1 = 36,
    SlapBass2 = 37,
    SynthBass1 = 38,
    SynthBass2 = 39,

    // Strings (40-47)
    Violin = 40,
    Viola = 41,
    Cello = 42,
    Contrabass = 43,
    TremoloStrings = 44,
    PizzicatoStrings = 45,
    OrchestralHarp = 46,
    Timpani = 47,

    // Ensemble (48-55)
    StringEnsemble1 = 48,
    StringEnsemble2 = 49,
    SynthStrings1 = 50,
    SynthStrings2 = 51,
    ChoirAahs = 52,
    VoiceOohs = 53,
    SynthVoice = 54,
    OrchestraHit = 55,

    // Brass (56-63)
    Trumpet = 56,
    Trombone = 57,
    Tuba = 58,
    MutedTrumpet = 59,
    FrenchHorn = 60,
    BrassSection = 61,
    SynthBrass1 = 62,
    SynthBrass2 = 63,

    // Reed (64-71)
    SopranoSax = 64,
    AltoSax = 65,
    TenorSax = 66,
    BaritoneSax = 67,
    Oboe = 68,
    EnglishHorn = 69,
    Bassoon = 70,
    Clarinet = 71,

    // Pipe (72-79)
    Piccolo = 72,
    Flute = 73,
    Recorder = 74,
    PanFlute = 75,
    BlownBottle = 76,
    Shakuhachi = 77,
    Whistle = 78,
    Ocarina = 79,

    // Synth Lead (80-87)
    Lead1Square = 80,
    Lead2Sawtooth = 81,
    Lead3Calliope = 82,
    Lead4Chiff = 83,
    Lead5Charang = 84,
    Lead6Voice = 85,
    Lead7Fifths = 86,
    Lead8BassAndLead = 87,

    // Synth Pad (88-95)
    Pad1NewAge = 88,
    Pad2Warm = 89,
    Pad3Polysynth = 90,
    Pad4Choir = 91,
    Pad5Bowed = 92,
    Pad6Metallic = 93,
    Pad7Halo = 94,
    Pad8Sweep = 95,

    // Synth Effects (96-103)
    Fx1Rain = 96,
    Fx2Soundtrack = 97,
    Fx3Crystal = 98,
    Fx4Atmosphere = 99,
    Fx5Brightness = 100,
    Fx6Goblins = 101,
    Fx7Echoes = 102,
    Fx8SciFi = 103,

    // Ethnic (104-111)
    Sitar = 104,
    Banjo = 105,
    Shamisen = 106,
    Koto = 107,
    Kalimba = 108,
    Bagpipe = 109,
    Fiddle = 110,
    Shanai = 111,

    // Percussive (112-119)
    TinkleBell = 112,
    Agogo = 113,
    SteelDrums = 114,
    Woodblock = 115,
    TaikoDrum = 116,
    MelodicTom = 117,
    SynthDrum = 118,
    ReverseCymbal = 119,

    // Sound Effects (120-127)
    GuitarFretNoise = 120,
    BreathNoise = 121,
    Seashore = 122,
    BirdTweet = 123,
    TelephoneRing = 124,
    Helicopter = 125,
    Applause = 126,
    Gunshot = 127
}

/// <summary>
/// Helper class for General MIDI instruments.
/// </summary>
public static class GeneralMidiHelper
{
    private static readonly string[] CategoryNames =
    [
        "Piano", "Chromatic Percussion", "Organ", "Guitar",
        "Bass", "Strings", "Ensemble", "Brass",
        "Reed", "Pipe", "Synth Lead", "Synth Pad",
        "Synth Effects", "Ethnic", "Percussive", "Sound Effects"
    ];

    /// <summary>
    /// Get the category name for a program number.
    /// </summary>
    public static string GetCategory(int program) => CategoryNames[Math.Clamp(program, 0, 127) / 8];

    /// <summary>
    /// Get a display name for a program (converts enum name to readable format).
    /// </summary>
    public static string GetDisplayName(GeneralMidiProgram program)
    {
        var name = program.ToString();
        // Insert spaces before capital letters
        var result = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (char.IsUpper(c) && result.Length > 0)
                result.Append(' ');
            result.Append(c);
        }
        return result.ToString();
    }

    /// <summary>
    /// Get all programs as a list of (number, name) tuples.
    /// </summary>
    public static IEnumerable<(int Number, string Name)> GetAllPrograms()
    {
        foreach (GeneralMidiProgram program in Enum.GetValues<GeneralMidiProgram>())
        {
            yield return ((int)program, GetDisplayName(program));
        }
    }
}
