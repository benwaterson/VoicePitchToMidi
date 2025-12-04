using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace VoicePitchToMidi.Core.PitchDetection;

/// <summary>
/// YIN pitch detection algorithm implementation optimized for voice.
/// Based on "YIN, a fundamental frequency estimator for speech and music" by de Cheveigné and Kawahara.
/// </summary>
public sealed class YinPitchDetector : IPitchDetector
{
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly float _threshold;
    private readonly float[] _yinBuffer;
    private readonly float _minFrequency;
    private readonly float _maxFrequency;
    private readonly int _minTau;
    private readonly int _maxTau;

    public YinPitchDetector(int sampleRate, int bufferSize = 2048, float threshold = 0.15f,
        float minFrequency = 50f, float maxFrequency = 1000f)
    {
        _sampleRate = sampleRate;
        _bufferSize = bufferSize;
        _threshold = threshold;
        _minFrequency = minFrequency;
        _maxFrequency = maxFrequency;
        _yinBuffer = new float[bufferSize / 2];

        // Calculate tau range based on frequency limits
        _minTau = Math.Max(2, (int)(_sampleRate / _maxFrequency));
        _maxTau = Math.Min(_yinBuffer.Length - 1, (int)(_sampleRate / _minFrequency));
    }

    public PitchResult DetectPitch(ReadOnlySpan<float> audioBuffer)
    {
        if (audioBuffer.Length < _bufferSize)
            return PitchResult.NoResult;

        // Step 1 & 2: Calculate difference function
        CalculateDifference(audioBuffer);

        // Step 3: Cumulative mean normalized difference
        CumulativeMeanNormalizedDifference();

        // Step 4: Absolute threshold
        int tau = AbsoluteThreshold();
        if (tau == -1)
            return PitchResult.NoResult;

        // Step 5: Parabolic interpolation for better precision
        float betterTau = ParabolicInterpolation(tau);

        // Calculate frequency
        float frequency = _sampleRate / betterTau;

        // Validate frequency is within expected range
        if (frequency < _minFrequency || frequency > _maxFrequency)
            return PitchResult.NoResult;

        // Calculate confidence (inverse of YIN value at detected tau)
        float confidence = 1f - _yinBuffer[tau];

        return new PitchResult(frequency, confidence, true);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CalculateDifference(ReadOnlySpan<float> audioBuffer)
    {
        int halfBuffer = _bufferSize / 2;
        Array.Clear(_yinBuffer);

        if (Avx2.IsSupported && halfBuffer >= 8)
        {
            CalculateDifferenceAvx2(audioBuffer, halfBuffer);
        }
        else
        {
            CalculateDifferenceScalar(audioBuffer, halfBuffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void CalculateDifferenceAvx2(ReadOnlySpan<float> audioBuffer, int halfBuffer)
    {
        for (int tau = 1; tau < halfBuffer; tau++)
        {
            float sum = 0f;
            int i = 0;

            // Process 8 floats at a time using AVX2
            int vectorLength = halfBuffer - 8;
            for (; i <= vectorLength; i += 8)
            {
                var v1 = Vector256.Create(
                    audioBuffer[i], audioBuffer[i + 1], audioBuffer[i + 2], audioBuffer[i + 3],
                    audioBuffer[i + 4], audioBuffer[i + 5], audioBuffer[i + 6], audioBuffer[i + 7]);
                var v2 = Vector256.Create(
                    audioBuffer[i + tau], audioBuffer[i + tau + 1], audioBuffer[i + tau + 2], audioBuffer[i + tau + 3],
                    audioBuffer[i + tau + 4], audioBuffer[i + tau + 5], audioBuffer[i + tau + 6], audioBuffer[i + tau + 7]);

                var diff = Avx.Subtract(v1, v2);
                var squared = Avx.Multiply(diff, diff);

                // Horizontal sum
                sum += Vector256.Sum(squared);
            }

            // Handle remaining elements
            for (; i < halfBuffer; i++)
            {
                float delta = audioBuffer[i] - audioBuffer[i + tau];
                sum += delta * delta;
            }

            _yinBuffer[tau] = sum;
        }
    }

    private void CalculateDifferenceScalar(ReadOnlySpan<float> audioBuffer, int halfBuffer)
    {
        for (int tau = 1; tau < halfBuffer; tau++)
        {
            float sum = 0f;
            for (int i = 0; i < halfBuffer; i++)
            {
                float delta = audioBuffer[i] - audioBuffer[i + tau];
                sum += delta * delta;
            }
            _yinBuffer[tau] = sum;
        }
    }

    private void CumulativeMeanNormalizedDifference()
    {
        _yinBuffer[0] = 1f;
        float runningSum = 0f;

        for (int tau = 1; tau < _yinBuffer.Length; tau++)
        {
            runningSum += _yinBuffer[tau];
            _yinBuffer[tau] = _yinBuffer[tau] * tau / runningSum;
        }
    }

    private int AbsoluteThreshold()
    {
        // Find first tau where value is below threshold and is a local minimum
        for (int tau = _minTau; tau < _maxTau; tau++)
        {
            if (_yinBuffer[tau] < _threshold)
            {
                // Find local minimum
                while (tau + 1 < _maxTau && _yinBuffer[tau + 1] < _yinBuffer[tau])
                {
                    tau++;
                }
                return tau;
            }
        }

        // If no value below threshold, find global minimum in range
        int minTau = _minTau;
        float minValue = _yinBuffer[_minTau];
        for (int tau = _minTau + 1; tau < _maxTau; tau++)
        {
            if (_yinBuffer[tau] < minValue)
            {
                minValue = _yinBuffer[tau];
                minTau = tau;
            }
        }

        // Only return if reasonably confident
        return minValue < 0.5f ? minTau : -1;
    }

    private float ParabolicInterpolation(int tau)
    {
        if (tau <= 0 || tau >= _yinBuffer.Length - 1)
            return tau;

        float s0 = _yinBuffer[tau - 1];
        float s1 = _yinBuffer[tau];
        float s2 = _yinBuffer[tau + 1];

        float adjustment = (s2 - s0) / (2f * (2f * s1 - s2 - s0));

        if (float.IsNaN(adjustment) || float.IsInfinity(adjustment))
            return tau;

        return tau + adjustment;
    }

    public void Reset()
    {
        Array.Clear(_yinBuffer);
    }
}
