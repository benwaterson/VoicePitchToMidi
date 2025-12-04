using System.Runtime.CompilerServices;

namespace VoicePitchToMidi.Core.PitchDetection;

/// <summary>
/// Simple autocorrelation-based pitch detection.
/// Fast but less accurate than YIN or MPM. Good for real-time with low latency requirements.
/// </summary>
public sealed class AutocorrelationPitchDetector : IPitchDetector
{
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly float[] _autocorrelation;
    private readonly float _minFrequency;
    private readonly float _maxFrequency;
    private readonly int _minLag;
    private readonly int _maxLag;
    private readonly float _threshold;

    public AutocorrelationPitchDetector(int sampleRate, int bufferSize = 2048,
        float minFrequency = 50f, float maxFrequency = 1000f, float threshold = 0.2f)
    {
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
        _minFrequency = minFrequency;
        _maxFrequency = maxFrequency;
        _threshold = threshold;
        _autocorrelation = new float[bufferSize / 2];

        _minLag = Math.Max(1, (int)(_sampleRate / _maxFrequency));
        _maxLag = Math.Min(_autocorrelation.Length - 1, (int)(_sampleRate / _minFrequency));
    }

    public PitchResult DetectPitch(ReadOnlySpan<float> audioBuffer)
    {
        if (audioBuffer.Length < _bufferSize)
            return PitchResult.NoResult;

        // Calculate autocorrelation
        CalculateAutocorrelation(audioBuffer);

        // Normalize by zero-lag value
        float zeroLag = _autocorrelation[0];
        if (zeroLag <= 0)
            return PitchResult.NoResult;

        // Find first peak after initial decline
        int bestLag = FindBestLag(zeroLag);

        if (bestLag == -1)
            return PitchResult.NoResult;

        // Parabolic interpolation
        float betterLag = ParabolicInterpolation(bestLag);

        // Calculate frequency
        float frequency = _sampleRate / betterLag;

        // Validate frequency range
        if (frequency < _minFrequency || frequency > _maxFrequency)
            return PitchResult.NoResult;

        // Confidence based on normalized autocorrelation at peak
        float confidence = _autocorrelation[bestLag] / zeroLag;

        return new PitchResult(frequency, confidence, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CalculateAutocorrelation(ReadOnlySpan<float> audioBuffer)
    {
        int halfBuffer = _bufferSize / 2;

        for (int lag = 0; lag < halfBuffer; lag++)
        {
            float sum = 0;
            for (int i = 0; i < halfBuffer; i++)
            {
                sum += audioBuffer[i] * audioBuffer[i + lag];
            }
            _autocorrelation[lag] = sum;
        }
    }

    private int FindBestLag(float zeroLag)
    {
        // Find first significant peak
        bool pastFirstMinimum = false;
        float lastValue = 1.0f;

        for (int lag = _minLag; lag <= _maxLag; lag++)
        {
            float normalized = _autocorrelation[lag] / zeroLag;

            // Wait until we've passed a minimum (signal has decorrelated)
            if (!pastFirstMinimum)
            {
                if (normalized > lastValue && lastValue < 0.5f)
                {
                    pastFirstMinimum = true;
                }
                lastValue = normalized;
                continue;
            }

            // Look for peak above threshold
            if (normalized > _threshold)
            {
                // Find local maximum
                while (lag + 1 <= _maxLag && _autocorrelation[lag + 1] > _autocorrelation[lag])
                {
                    lag++;
                }
                return lag;
            }

            lastValue = normalized;
        }

        // Fallback: find global maximum in range
        int bestLag = _minLag;
        float bestValue = _autocorrelation[_minLag];

        for (int lag = _minLag + 1; lag <= _maxLag; lag++)
        {
            if (_autocorrelation[lag] > bestValue)
            {
                bestValue = _autocorrelation[lag];
                bestLag = lag;
            }
        }

        // Only return if reasonably confident
        return (bestValue / zeroLag) > _threshold ? bestLag : -1;
    }

    private float ParabolicInterpolation(int lag)
    {
        if (lag <= 0 || lag >= _autocorrelation.Length - 1)
            return lag;

        float s0 = _autocorrelation[lag - 1];
        float s1 = _autocorrelation[lag];
        float s2 = _autocorrelation[lag + 1];

        float denominator = 2 * (2 * s1 - s2 - s0);
        if (Math.Abs(denominator) < 1e-10f)
            return lag;

        float adjustment = (s2 - s0) / denominator;

        if (float.IsNaN(adjustment) || float.IsInfinity(adjustment) || Math.Abs(adjustment) > 1)
            return lag;

        return lag + adjustment;
    }

    public void Reset()
    {
        Array.Clear(_autocorrelation);
    }
}
