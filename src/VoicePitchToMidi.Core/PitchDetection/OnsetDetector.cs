using System.Numerics;

namespace VoicePitchToMidi.Core.PitchDetection;

/// <summary>
/// Fast onset detector using energy ratio and spectral flux on a short window (256 samples).
/// Designed to detect percussive transients before the pitch detector resolves.
/// </summary>
internal sealed class OnsetDetector
{
    private const int FftSize = 256;
    private const int HalfFft = FftSize / 2;

    // Energy tracking
    private float _longTermEnergy;
    private const float EnergyDecay = 0.995f;
    private const float EnergyAttack = 0.05f;

    // Spectral flux
    private readonly float[] _prevMagnitudes = new float[HalfFft + 1];

    // Adaptive threshold
    private float _threshold;
    private const float ThresholdDecay = 0.97f;
    private const float ThresholdRaise = 1.5f;
    private const float MinThreshold = 0.02f;

    // FFT working buffers
    private readonly float[] _fftReal = new float[FftSize];
    private readonly float[] _fftImag = new float[FftSize];
    private readonly float[] _magnitudes = new float[HalfFft + 1];

    // Pre-computed twiddle factors
    private readonly float[] _twiddleReal;
    private readonly float[] _twiddleImag;

    public OnsetDetector()
    {
        // Pre-compute twiddle factors for radix-2 FFT
        _twiddleReal = new float[HalfFft];
        _twiddleImag = new float[HalfFft];
        for (int i = 0; i < HalfFft; i++)
        {
            double angle = -2.0 * Math.PI * i / FftSize;
            _twiddleReal[i] = (float)Math.Cos(angle);
            _twiddleImag[i] = (float)Math.Sin(angle);
        }
    }

    /// <summary>
    /// Detect onset in the given audio buffer.
    /// Pass the most recent 256 samples.
    /// </summary>
    /// <param name="buffer">Audio samples (at least 256)</param>
    /// <param name="onsetRms">RMS energy at onset time (for velocity mapping)</param>
    /// <returns>Onset strength: 0 = no onset, >0 = onset detected (higher = stronger)</returns>
    public float Detect(ReadOnlySpan<float> buffer, out float onsetRms)
    {
        int len = Math.Min(buffer.Length, FftSize);

        // 1. Compute short-term RMS on most recent 128 samples
        float shortRms = ComputeRms(buffer.Slice(Math.Max(0, len - 128)));
        onsetRms = shortRms;

        // 2. Update long-term energy (exponential moving average)
        _longTermEnergy = _longTermEnergy * EnergyDecay + shortRms * shortRms * EnergyAttack;
        float longTermRms = MathF.Sqrt(_longTermEnergy);

        // 3. Energy ratio
        float energyRatio = longTermRms > 1e-8f ? shortRms / longTermRms : 0f;

        // 4. Spectral flux via inline 256-point FFT
        for (int i = 0; i < FftSize; i++)
        {
            _fftReal[i] = i < len ? buffer[i] : 0f;
            _fftImag[i] = 0f;
        }

        // Apply Hann window
        for (int i = 0; i < FftSize; i++)
        {
            float window = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (FftSize - 1)));
            _fftReal[i] *= window;
        }

        Fft256(_fftReal, _fftImag);

        // Compute magnitudes and positive spectral flux
        float flux = 0f;
        for (int i = 0; i <= HalfFft; i++)
        {
            float mag = MathF.Sqrt(_fftReal[i] * _fftReal[i] + _fftImag[i] * _fftImag[i]);
            _magnitudes[i] = mag;

            float diff = mag - _prevMagnitudes[i];
            if (diff > 0)
                flux += diff;

            _prevMagnitudes[i] = mag;
        }

        // Normalize flux by bin count
        flux /= (HalfFft + 1);

        // 5. Combine energy ratio and spectral flux
        float combined = energyRatio * 0.4f + flux * 20f * 0.6f;

        // 6. Apply adaptive threshold
        _threshold *= ThresholdDecay;
        if (_threshold < MinThreshold)
            _threshold = MinThreshold;

        if (combined > _threshold)
        {
            // Raise threshold to prevent re-triggering
            _threshold = combined * ThresholdRaise;
            return combined;
        }

        return 0f;
    }

    public int BinCount => HalfFft + 1;

    public void CopyMagnitudesTo(Span<float> dest)
    {
        _magnitudes.AsSpan(0, Math.Min(dest.Length, HalfFft + 1)).CopyTo(dest);
    }

    /// <summary>
    /// Reset detector state.
    /// </summary>
    public void Reset()
    {
        _longTermEnergy = 0f;
        _threshold = MinThreshold;
        Array.Clear(_prevMagnitudes);
    }

    private static float ComputeRms(ReadOnlySpan<float> buffer)
    {
        float sum = 0f;
        foreach (float s in buffer)
            sum += s * s;
        return MathF.Sqrt(sum / buffer.Length);
    }

    /// <summary>
    /// In-place radix-2 DIT FFT for 256 points.
    /// </summary>
    private void Fft256(float[] real, float[] imag)
    {
        // Bit-reversal permutation
        int n = FftSize;
        int halfN = n >> 1;
        int j = 0;
        for (int i = 0; i < n - 1; i++)
        {
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
            int k = halfN;
            while (k <= j)
            {
                j -= k;
                k >>= 1;
            }
            j += k;
        }

        // Butterfly stages
        for (int stage = 1; stage < n; stage <<= 1)
        {
            int twiddleStep = n / (stage << 1);
            for (int group = 0; group < n; group += stage << 1)
            {
                int twiddleIdx = 0;
                for (int pair = 0; pair < stage; pair++)
                {
                    int even = group + pair;
                    int odd = even + stage;

                    float tr = _twiddleReal[twiddleIdx] * real[odd] - _twiddleImag[twiddleIdx] * imag[odd];
                    float ti = _twiddleReal[twiddleIdx] * imag[odd] + _twiddleImag[twiddleIdx] * real[odd];

                    real[odd] = real[even] - tr;
                    imag[odd] = imag[even] - ti;
                    real[even] += tr;
                    imag[even] += ti;

                    twiddleIdx += twiddleStep;
                }
            }
        }
    }
}
