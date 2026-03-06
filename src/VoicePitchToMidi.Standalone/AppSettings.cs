using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoicePitchToMidi.Core.PitchDetection;

namespace VoicePitchToMidi.Standalone;

/// <summary>
/// Application settings that persist between sessions.
/// </summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoicePitchToMidi",
        "settings.json");

    // Device settings
    public string AudioBackend { get; set; } = "WASAPI";
    public string? AudioDeviceId { get; set; }
    public string? MidiDeviceName { get; set; }

    // Algorithm
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PitchAlgorithm Algorithm { get; set; } = PitchAlgorithm.Yin;

    // Instrument
    public int InstrumentProgram { get; set; } = 0;

    // Processing settings
    public float NoiseGate { get; set; } = 0.01f;
    public float MinConfidence { get; set; } = 0.8f;
    public float Smoothing { get; set; } = 0.3f;
    public int NoteStability { get; set; } = 2;
    public int MinNote { get; set; } = 36;
    public int MaxNote { get; set; } = 84;
    public bool SendPitchBend { get; set; } = true;
    public float VelocitySensitivity { get; set; } = 1.0f;
    public int MidiChannel { get; set; } = 1;
    public bool PercussiveMode { get; set; }

    /// <summary>
    /// Load settings from disk, or return defaults if not found.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? new AppSettings();
            }
        }
        catch
        {
            // If loading fails, return defaults
        }

        return new AppSettings();
    }

    /// <summary>
    /// Save settings to disk.
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors silently
        }
    }
}
