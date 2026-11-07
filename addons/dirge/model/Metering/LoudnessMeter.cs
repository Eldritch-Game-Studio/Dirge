using System;
using System.Collections.Generic;

namespace Dirge.Metering;

/// <summary>
/// ITU-R BS.1770-4 loudness and true-peak meter, pure C#.
/// <list type="bullet">
/// <item>K-weighting: stage 1 high shelf (+4 dB above ~1.5 kHz) and stage 2 RLB high-pass (~38 Hz), biquad coefficients
/// derived analytically for the given sample rate (bilinear transform with the ITU reference frequencies/Q; at 48 kHz they
/// reproduce the published table).</item>
/// <item>Gating blocks of 400 ms stepped every 100 ms (75 % overlap). <see cref="MomentaryLufs"/> is the latest block,
/// <see cref="ShortTermLufs"/> the energy of the last 3 s, <see cref="IntegratedLufs"/> the mean energy of the blocks that
/// pass the −70 LUFS absolute gate and the −10 LU relative gate (relative threshold from the absolutely gated mean).</item>
/// <item>Channel weights per BS.1770-4: L/R/C 1.0, Ls/Rs 1.41, LFE excluded. Supported layouts: 1 (mono), 2 (L R) and
/// 6 (L R C LFE Ls Rs). Loudness is −0.691 + 10·log10(Σ Gᵢ zᵢ).</item>
/// <item>True peak: 4× oversampling with the 48-tap polyphase FIR of BS.1770-4 Annex 2 (12 taps per phase), max |sample| in
/// dBTP. The same filter is used for 44.1 kHz (the Recommendation requires ≥ 4× for 48 kHz-class rates).</item>
/// </list>
/// Steady-state <see cref="Push"/> allocates only when a new 100 ms block is appended to the integrated history (amortized
/// list growth, ~10 doubles per second).
/// </summary>
public sealed class LoudnessMeter
{
    private const double AbsoluteGateLufs = -70.0;
    private const double RelativeGateLu = -10.0;
    private const double LoudnessOffset = -0.691;
    private const int StepsPerBlock = 4;      // 400 ms / 100 ms
    private const int StepsPerShortTerm = 30; // 3 s / 100 ms
    private const int Oversampling = 4;
    private const int Taps = 12;

    /// <summary>BS.1770-4 Annex 2 interpolation filter, phase-major (4 phases × 12 taps).</summary>
    private static readonly float[] TruePeakFir =
    {
        0.0017089843750f, 0.0109863281250f, -0.0196533203125f, 0.0332031250000f, -0.0594482421875f, 0.1373291015625f,
        0.9721679687500f, -0.1022949218750f, 0.0476074218750f, -0.0266113281250f, 0.0148925781250f, -0.0083007812500f,
        -0.0291748046875f, 0.0292968750000f, -0.0517578125000f, 0.0891113281250f, -0.1665039062500f, 0.4650878906250f,
        0.7797851562500f, -0.2003173828125f, 0.1015625000000f, -0.0582275390625f, 0.0330810546875f, -0.0189208984375f,
        -0.0189208984375f, 0.0330810546875f, -0.0582275390625f, 0.1015625000000f, -0.2003173828125f, 0.7797851562500f,
        0.4650878906250f, -0.1665039062500f, 0.0891113281250f, -0.0517578125000f, 0.0292968750000f, -0.0291748046875f,
        -0.0083007812500f, 0.0148925781250f, -0.0266113281250f, 0.0476074218750f, -0.1022949218750f, 0.9721679687500f,
        0.1373291015625f, -0.0594482421875f, 0.0332031250000f, -0.0196533203125f, 0.0109863281250f, 0.0017089843750f,
    };

    private readonly int _channels;
    private readonly double[] _weights;
    // K-weighting biquads (direct form II transposed), per channel.
    private readonly double _b0a, _b1a, _b2a, _a1a, _a2a;
    private readonly double _b0b, _b1b, _b2b, _a1b, _a2b;
    private readonly double[] _z1a, _z2a, _z1b, _z2b;
    // 100 ms step accumulation.
    private readonly int _stepSamples;
    private readonly double[] _stepEnergy;        // Σ x² per channel in the current step
    private int _stepFill;
    // Ring of the last 30 steps' weighted energy sums (Σ Gᵢ Σ xᵢ²), oldest first.
    private readonly double[] _stepRing = new double[StepsPerShortTerm];
    private int _ringHead;
    private int _ringCount;
    // Integrated history: weighted mean-square power of every 400 ms block.
    private readonly List<double> _blockPower = new(4096);
    private bool _integratedDirty = true;
    private double _integrated = double.NegativeInfinity;
    // True peak.
    private readonly float[] _history;            // Taps samples per channel (ring)
    private int _historyPos;
    private float _truePeak;

    public int SampleRate { get; }
    public int Channels => _channels;

    public LoudnessMeter(int sampleRate, int channels)
    {
        if (sampleRate < 8000) throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be at least 8 kHz.");
        if (channels is not (1 or 2 or 6)) throw new ArgumentOutOfRangeException(nameof(channels), "Supported layouts: 1, 2 or 6 channels.");
        SampleRate = sampleRate;
        _channels = channels;
        _weights = channels switch
        {
            1 => new[] { 1.0 },
            2 => new[] { 1.0, 1.0 },
            _ => new[] { 1.0, 1.0, 1.0, 0.0, 1.41, 1.41 },
        };

        // Stage 1: high shelf. Reference design values from the ITU filter (f0, Q, gain), bilinear-transformed to fs.
        {
            const double f0 = 1681.974450955533, g = 3.999843853973347, q = 0.7071752369554196;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double vh = Math.Pow(10.0, g / 20.0);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1.0 + k / q + k * k;
            _b0a = (vh + vb * k / q + k * k) / a0;
            _b1a = 2.0 * (k * k - vh) / a0;
            _b2a = (vh - vb * k / q + k * k) / a0;
            _a1a = 2.0 * (k * k - 1.0) / a0;
            _a2a = (1.0 - k / q + k * k) / a0;
        }
        // Stage 2: RLB high-pass.
        {
            const double f0 = 38.13547087602444, q = 0.5003270373238773;
            double k = Math.Tan(Math.PI * f0 / sampleRate);
            double a0 = 1.0 + k / q + k * k;
            _b0b = 1.0 / a0;
            _b1b = -2.0 / a0;
            _b2b = 1.0 / a0;
            _a1b = 2.0 * (k * k - 1.0) / a0;
            _a2b = (1.0 - k / q + k * k) / a0;
        }

        _z1a = new double[channels]; _z2a = new double[channels];
        _z1b = new double[channels]; _z2b = new double[channels];
        _stepSamples = Math.Max(1, (int)Math.Round(sampleRate * 0.1));
        _stepEnergy = new double[channels];
        _history = new float[Taps * channels];
        Reset();
    }

    /// <summary>Loudness of the latest 400 ms block; −∞ before the first block or for digital silence.</summary>
    public float MomentaryLufs { get; private set; } = float.NegativeInfinity;
    /// <summary>Loudness of the last 3 s; −∞ until 3 s of audio have been pushed.</summary>
    public float ShortTermLufs { get; private set; } = float.NegativeInfinity;
    /// <summary>Gated integrated loudness over everything pushed since <see cref="Reset"/>; −∞ when no block passes the gates.</summary>
    public float IntegratedLufs
    {
        get
        {
            if (_integratedDirty) { _integrated = ComputeIntegrated(); _integratedDirty = false; }
            return (float)_integrated;
        }
    }
    /// <summary>Maximum inter-sample peak since <see cref="Reset"/> in dBTP; −∞ for silence.</summary>
    public float TruePeakDbtp => _truePeak > 0f ? 20f * MathF.Log10(_truePeak) : float.NegativeInfinity;

    public void Reset()
    {
        Array.Clear(_z1a); Array.Clear(_z2a); Array.Clear(_z1b); Array.Clear(_z2b);
        Array.Clear(_stepEnergy);
        _stepFill = 0;
        Array.Clear(_stepRing);
        _ringHead = 0;
        _ringCount = 0;
        _blockPower.Clear();
        _integratedDirty = true;
        _integrated = double.NegativeInfinity;
        Array.Clear(_history);
        _historyPos = 0;
        _truePeak = 0f;
        MomentaryLufs = float.NegativeInfinity;
        ShortTermLufs = float.NegativeInfinity;
    }

    /// <summary>Feed interleaved samples (frames × <see cref="Channels"/>); a trailing partial frame is ignored.</summary>
    public void Push(ReadOnlySpan<float> interleaved)
    {
        int channels = _channels;
        int frames = interleaved.Length / channels;
        for (int f = 0; f < frames; f++)
        {
            int baseIndex = f * channels;
            for (int c = 0; c < channels; c++)
            {
                float sample = interleaved[baseIndex + c];
                TruePeakSample(c, sample);
                if (_weights[c] == 0.0) continue;
                // Stage 1.
                double x = sample;
                double y = _b0a * x + _z1a[c];
                _z1a[c] = _b1a * x - _a1a * y + _z2a[c];
                _z2a[c] = _b2a * x - _a2a * y;
                // Stage 2.
                double w = _b0b * y + _z1b[c];
                _z1b[c] = _b1b * y - _a1b * w + _z2b[c];
                _z2b[c] = _b2b * y - _a2b * w;
                _stepEnergy[c] += w * w;
            }
            if (++_stepFill >= _stepSamples) CompleteStep();
        }
    }

    /// <summary>Write the sample into the channel's 12-sample history and evaluate the 4 interpolation phases around it.</summary>
    private void TruePeakSample(int channel, float sample)
    {
        float[] h = _history;
        int offset = channel * Taps;
        int newest = _historyPos;
        h[offset + newest] = sample;
        for (int phase = 0; phase < Oversampling; phase++)
        {
            double acc = 0;
            int firBase = phase * Taps;
            int idx = newest;
            for (int k = 0; k < Taps; k++)
            {
                acc += TruePeakFir[firBase + k] * h[offset + idx];
                idx = idx == 0 ? Taps - 1 : idx - 1;
            }
            float mag = (float)Math.Abs(acc);
            if (mag > _truePeak) _truePeak = mag;
        }
        // All channels of a frame share one write position; advance it after the last channel.
        if (channel == _channels - 1) _historyPos = (_historyPos + 1) % Taps;
    }

    private void CompleteStep()
    {
        double weighted = 0;
        for (int c = 0; c < _channels; c++)
        {
            weighted += _weights[c] * _stepEnergy[c];
            _stepEnergy[c] = 0;
        }
        _stepFill = 0;
        _stepRing[_ringHead] = weighted;
        _ringHead = (_ringHead + 1) % StepsPerShortTerm;
        if (_ringCount < StepsPerShortTerm) _ringCount++;

        if (_ringCount >= StepsPerBlock)
        {
            double block = SumLastSteps(StepsPerBlock) / (StepsPerBlock * (double)_stepSamples);
            MomentaryLufs = (float)ToLufs(block);
            _blockPower.Add(block);
            _integratedDirty = true;
        }
        if (_ringCount >= StepsPerShortTerm)
            ShortTermLufs = (float)ToLufs(SumLastSteps(StepsPerShortTerm) / (StepsPerShortTerm * (double)_stepSamples));
    }

    private double SumLastSteps(int count)
    {
        double sum = 0;
        int idx = _ringHead;
        for (int i = 0; i < count; i++)
        {
            idx = idx == 0 ? StepsPerShortTerm - 1 : idx - 1;
            sum += _stepRing[idx];
        }
        return sum;
    }

    private double ComputeIntegrated()
    {
        List<double> blocks = _blockPower;
        if (blocks.Count == 0) return double.NegativeInfinity;
        double absoluteGate = FromLufs(AbsoluteGateLufs);
        double sum = 0;
        int n = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] <= absoluteGate) continue;
            sum += blocks[i];
            n++;
        }
        if (n == 0) return double.NegativeInfinity;
        double relativeGate = FromLufs(ToLufs(sum / n) + RelativeGateLu);
        double threshold = Math.Max(absoluteGate, relativeGate);
        sum = 0;
        n = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (blocks[i] <= threshold) continue;
            sum += blocks[i];
            n++;
        }
        return n == 0 ? double.NegativeInfinity : ToLufs(sum / n);
    }

    private static double ToLufs(double power) => power > 0 ? LoudnessOffset + 10.0 * Math.Log10(power) : double.NegativeInfinity;

    private static double FromLufs(double lufs) => Math.Pow(10.0, (lufs - LoudnessOffset) / 10.0);
}
