using System.Runtime.CompilerServices;

namespace VoicePitchToMidi.Core.PitchDetection;

/// <summary>
/// McLeod Pitch Method (MPM) - improved autocorrelation algorithm.
/// Based on "A Smarter Way to Find Pitch" by Philip McLeod and Geoff Wyvill.
/// Good balance between speed and accuracy.
/// </summary>
public sealed class McLeodPitchDetector : IPitchDetector
{
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly float _cutoff;
    private readonly float _smallCutoff;
    private readonly float[] _nsdf;
    private readonly float _minFrequency;
    private readonly float _maxFrequency;

    public McLeodPitchDetector(int sampleRate, int bufferSize = 2048, float cutoff = 0.93f,
        float smallCutoff = 0.5f, float minFrequency = 50f, float maxFrequency = 1000f)
    {
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
        _cutoff = cutoff;
        _smallCutoff = smallCutoff;
        _minFrequency = minFrequency;
        _maxFrequency = maxFrequency;
        _nsdf = new float[bufferSize];
    }

    public PitchResult DetectPitch(ReadOnlySpan<float> audioBuffer)
    {
        if (audioBuffer.Length < _bufferSize)
            return PitchResult.NoResult;

        // Calculate Normalized Square Difference Function (NSDF)
        CalculateNsdf(audioBuffer);

        // Find peaks in NSDF
        var peaks = FindPeaks();

        if (peaks.Count == 0)
            return PitchResult.NoResult;

        // Find the highest peak that exceeds the cutoff
        float highestPeak = 0;
        int bestTau = -1;

        foreach (var (tau, value) in peaks)
        {
            if (value > highestPeak)
            {
                highestPeak = value;
                bestTau = tau;
            }
        }

        if (bestTau == -1 || highestPeak < _smallCutoff)
            return PitchResult.NoResult;

        // Find the first peak that exceeds cutoff * highest peak
        float threshold = _cutoff * highestPeak;
        foreach (var (tau, value) in peaks)
        {
            if (value >= threshold)
            {
                bestTau = tau;
                break;
            }
        }

        // Parabolic interpolation for better precision
        float betterTau = ParabolicInterpolation(bestTau);

        // Calculate frequency
        float frequency = _sampleRate / betterTau;

        // Validate frequency range
        if (frequency < _minFrequency || frequency > _maxFrequency)
            return PitchResult.NoResult;

        return new PitchResult(frequency, highestPeak, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CalculateNsdf(ReadOnlySpan<float> audioBuffer)
    {
        int size = _bufferSize;

        for (int tau = 0; tau < size; tau++)
        {
            float acf = 0; // Autocorrelation
            float divisorM = 0;

            for (int i = 0; i < size - tau; i++)
            {
                acf += audioBuffer[i] * audioBuffer[i + tau];
                divisorM += audioBuffer[i] * audioBuffer[i] + audioBuffer[i + tau] * audioBuffer[i + tau];
            }

            _nsdf[tau] = divisorM > 0 ? 2 * acf / divisorM : 0;
        }
    }

    private List<(int tau, float value)> FindPeaks()
    {
        var peaks = new List<(int tau, float value)>();

        int minTau = Math.Max(2, (int)(_sampleRate / _maxFrequency));
        int maxTau = Math.Min(_bufferSize - 1, (int)(_sampleRate / _minFrequency));

        bool positive = false;
        int peakStart = 0;

        for (int tau = minTau; tau < maxTau; tau++)
        {
            if (_nsdf[tau] > 0)
            {
                if (!positive)
                {
                    positive = true;
                    peakStart = tau;
                }
            }
            else if (positive)
            {
                // Find maximum in this positive region
                int maxTauInRegion = peakStart;
                float maxValue = _nsdf[peakStart];

                for (int i = peakStart + 1; i < tau; i++)
                {
                    if (_nsdf[i] > maxValue)
                    {
                        maxValue = _nsdf[i];
                        maxTauInRegion = i;
                    }
                }

                peaks.Add((maxTauInRegion, maxValue));
                positive = false;
            }
        }

        return peaks;
    }

    private float ParabolicInterpolation(int tau)
    {
        if (tau <= 0 || tau >= _nsdf.Length - 1)
            return tau;

        float s0 = _nsdf[tau - 1];
        float s1 = _nsdf[tau];
        float s2 = _nsdf[tau + 1];

        float adjustment = (s2 - s0) / (2 * (2 * s1 - s2 - s0));

        if (float.IsNaN(adjustment) || float.IsInfinity(adjustment))
            return tau;

        return tau + adjustment;
    }

    public void Reset()
    {
        Array.Clear(_nsdf);
    }
}
