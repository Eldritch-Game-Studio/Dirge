using System;
using Dirge.Project;

namespace Dirge.Evaluation;

/// <summary>Deterministic xorshift64* generator; one per system so command streams replay bit-identically for a seed.</summary>
public sealed class DirgeRandom
{
    private ulong _state;
    public DirgeRandom(int seed) { _state = ((seed == 0 ? 0x9E3779B97F4A7C15UL : (ulong)(uint)seed) * 0x2545F4914F6CDD1DUL) | 1UL; }
    public ulong NextULong()
    {
        ulong x = _state;
        x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
        _state = x;
        return x * 0x2545F4914F6CDD1DUL;
    }
    /// <summary>[0,1)</summary>
    public float NextFloat() => (NextULong() >> 40) * (1f / (1 << 24));
    /// <summary>[-1,1]</summary>
    public float NextSigned() => NextFloat() * 2f - 1f;
    public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextULong() % (ulong)maxExclusive);
    public bool Chance(float probability) => probability >= 1f || (probability > 0f && NextFloat() < probability);
}

/// <summary>Stateful AHDSR evaluator. Output 0..1. <see cref="Release"/> begins the release stage from the current level.</summary>
public struct AhdsrState
{
    private readonly Ahdsr _env;
    private double _time;
    private bool _released;
    private float _releaseFrom;
    private double _releaseStart;

    public AhdsrState(Ahdsr envelope) { _env = envelope; _time = 0; _released = false; _releaseFrom = 0f; _releaseStart = 0; }

    public bool IsReleased => _released;
    public bool IsFinished => _released && _time - _releaseStart >= _env.Release;

    public float Advance(double dt)
    {
        _time += dt;
        return Current;
    }

    public void Release()
    {
        if (_released) return;
        _releaseFrom = Current;
        _released = true;
        _releaseStart = _time;
    }

    public float Current
    {
        get
        {
            if (_released)
            {
                if (_env.Release <= 0f) return 0f;
                float t = (float)((_time - _releaseStart) / _env.Release);
                return t >= 1f ? 0f : _releaseFrom * (1f - t);
            }
            double t0 = _time;
            if (t0 < _env.Attack) return _env.Attack <= 0f ? 1f : (float)(t0 / _env.Attack);
            t0 -= _env.Attack;
            if (t0 < _env.Hold) return 1f;
            t0 -= _env.Hold;
            if (t0 < _env.Decay) return 1f + (_env.Sustain - 1f) * (float)(t0 / _env.Decay);
            return _env.Sustain;
        }
    }
}

/// <summary>LFO phase accumulator. Output -1..1.</summary>
public struct LfoState
{
    private readonly LfoModulator _lfo;
    private double _phase;
    private float _noise;
    private readonly DirgeRandom? _random;

    public LfoState(LfoModulator lfo, DirgeRandom? random)
    {
        _lfo = lfo; _phase = lfo.Phase; _random = random; _noise = random?.NextSigned() ?? 0f;
    }

    /// <param name="rateHz">Effective rate; the caller resolves tempo sync.</param>
    public float Advance(double dt, float rateHz)
    {
        double previous = _phase;
        _phase = (_phase + dt * rateHz) % 1.0;
        if (_phase < previous && _random is not null) _noise = _random.NextSigned();
        return Current;
    }

    public float Current
    {
        get
        {
            float p = (float)_phase;
            return _lfo.Shape switch
            {
                LfoShape.Sine => MathF.Sin(p * MathF.Tau),
                LfoShape.Square => p < 0.5f ? 1f : -1f,
                LfoShape.Triangle => p < 0.5f ? p * 4f - 1f : 3f - p * 4f,
                LfoShape.SawUp => p * 2f - 1f,
                LfoShape.SawDown => 1f - p * 2f,
                LfoShape.Noise => _noise,
                _ => 0f,
            };
        }
    }
}

/// <summary>Pure autopitch evaluation: pitch ratio for a parameter value (see <see cref="AutopitchModulator"/>).</summary>
public static class AutopitchMath
{
    /// <summary>
    /// Piecewise-linear ratio through (min, PitchAtMinimum) → (reference, 1) → (max, PitchAtMaximum); <paramref name="value"/>
    /// is clamped to [min, max]. A reference equal to an endpoint collapses that side to 1.
    /// </summary>
    public static float Ratio(AutopitchModulator modulator, float minimum, float maximum, float value)
    {
        float reference = Math.Clamp(modulator.ReferenceValue, Math.Min(minimum, maximum), Math.Max(minimum, maximum));
        value = Math.Clamp(value, Math.Min(minimum, maximum), Math.Max(minimum, maximum));
        if (value == reference) return 1f;
        if (value < reference)
        {
            float span = reference - minimum;
            return span <= 0f ? 1f : modulator.PitchAtMinimum + (1f - modulator.PitchAtMinimum) * ((value - minimum) / span);
        }
        else
        {
            float span = maximum - reference;
            return span <= 0f ? 1f : 1f + (modulator.PitchAtMaximum - 1f) * ((value - reference) / span);
        }
    }

    /// <summary>Ratio with <see cref="Modulator.Amount"/> applied to the deviation from 1.</summary>
    public static float Evaluate(AutopitchModulator modulator, float minimum, float maximum, float value)
        => 1f + modulator.Amount * (Ratio(modulator, minimum, maximum, value) - 1f);
}
