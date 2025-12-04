namespace VoicePitchToMidi.Core.PitchDetection;

/// <summary>
/// Available pitch detection algorithms.
/// </summary>
public enum PitchAlgorithm
{
    /// <summary>
    /// YIN algorithm - most accurate for voice, slightly higher latency (~25ms).
    /// Best for: singing, speech, monophonic instruments.
    /// </summary>
    Yin,

    /// <summary>
    /// McLeod Pitch Method - good balance of speed and accuracy.
    /// Best for: general purpose, real-time applications.
    /// </summary>
    McLeod,

    /// <summary>
    /// Simple autocorrelation - fastest but less accurate.
    /// Best for: low-latency applications where some error is acceptable.
    /// </summary>
    Autocorrelation
}

/// <summary>
/// Factory for creating pitch detectors.
/// </summary>
public static class PitchDetectorFactory
{
    public static IPitchDetector Create(PitchAlgorithm algorithm, int sampleRate, int bufferSize = 2048,
        float minFrequency = 50f, float maxFrequency = 1000f)
    {
        return algorithm switch
        {
            PitchAlgorithm.Yin => new YinPitchDetector(sampleRate, bufferSize, 0.15f, minFrequency, maxFrequency),
            PitchAlgorithm.McLeod => new McLeodPitchDetector(sampleRate, bufferSize, 0.93f, 0.5f, minFrequency, maxFrequency),
            PitchAlgorithm.Autocorrelation => new AutocorrelationPitchDetector(sampleRate, bufferSize, minFrequency, maxFrequency, 0.2f),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm))
        };
    }
}
