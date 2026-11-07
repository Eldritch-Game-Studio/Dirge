using System;
using Dirge.Metering;
using Godot;

namespace Dirge.Driver;

/// <summary>
/// Measures the program loudness of the Master bus: an <see cref="AudioEffectCapture"/> appended after the authored
/// effect chain (so authored effect indices are untouched) is drained every frame into a <see cref="LoudnessMeter"/>.
/// The capture is re-appended by <see cref="AudioDriver.LoadMixer"/> after each mixer rebuild. The only steady-state
/// allocation is the <c>Vector2[]</c> returned by <see cref="AudioEffectCapture.GetBuffer"/>; the stock C# binding has no
/// span overload, and the interleaved float buffer handed to the meter is reused.
/// </summary>
public sealed class LoudnessCapture : IDisposable
{
    /// <summary>Seconds of audio the capture ring keeps; generous so a long frame hitch does not drop samples.</summary>
    private const float BufferSeconds = 0.5f;
    private const int MaxFramesPerPoll = 1 << 16;

    private readonly AudioEffectCapture _effect = new() { BufferLength = BufferSeconds };
    private LoudnessMeter _meter;
    private float[] _interleaved = new float[4096];
    private int _sampleRate;

    public LoudnessCapture()
    {
        _sampleRate = (int)Math.Round(AudioServer.GetMixRate());
        _meter = new LoudnessMeter(_sampleRate, 2);
    }

    /// <summary>The meter being fed; read <c>MomentaryLufs</c> etc. from it.</summary>
    public LoudnessMeter Meter => _meter;

    public float MomentaryLufs => _meter.MomentaryLufs;
    public float ShortTermLufs => _meter.ShortTermLufs;
    public float IntegratedLufs => _meter.IntegratedLufs;
    public float TruePeakDbtp => _meter.TruePeakDbtp;

    /// <summary>True while the capture effect sits on bus 0.</summary>
    public bool IsInstalled => IndexOnMaster() >= 0;

    /// <summary>Appends the capture effect to the Master bus (after every authored effect) unless it is already there.</summary>
    public void Install()
    {
        if (IndexOnMaster() >= 0)
        {
            return;
        }

        int rate = (int)Math.Round(AudioServer.GetMixRate());
        if (rate != _sampleRate)
        {
            _sampleRate = rate;
            _meter = new LoudnessMeter(rate, 2);
        }

        AudioServer.AddBusEffect(0, _effect);
        _effect.ClearBuffer();
    }

    public void Uninstall()
    {
        int index = IndexOnMaster();
        if (index >= 0)
        {
            AudioServer.RemoveBusEffect(0, index);
        }
    }

    /// <summary>Integrated loudness and gating history restart; momentary/short-term windows are flushed too.</summary>
    public void Reset()
    {
        _meter.Reset();
        _effect.ClearBuffer();
    }

    /// <summary>Removes the effect from Master and releases the managed capture resource (otherwise reported as leaked at exit).</summary>
    public void Dispose()
    {
        Uninstall();
        _effect.Dispose();
    }

    /// <summary>Drains everything the mixer produced since the last call into the meter. Call once per frame.</summary>
    public void Poll()
    {
        int frames = _effect.GetFramesAvailable();
        if (frames <= 0)
        {
            return;
        }

        if (frames > MaxFramesPerPoll)
        {
            frames = MaxFramesPerPoll;
        }

        Vector2[] stereo = _effect.GetBuffer(frames);
        int samples = stereo.Length * 2;
        if (_interleaved.Length < samples)
        {
            Array.Resize(ref _interleaved, Math.Max(samples, _interleaved.Length * 2));
        }

        for (int i = 0, s = 0; i < stereo.Length; i++, s += 2)
        {
            _interleaved[s] = stereo[i].X;
            _interleaved[s + 1] = stereo[i].Y;
        }

        _meter.Push(new ReadOnlySpan<float>(_interleaved, 0, samples));
    }

    private int IndexOnMaster()
    {
        ulong id = _effect.GetInstanceId();
        int count = AudioServer.GetBusEffectCount(0);
        for (int i = 0; i < count; i++)
        {
            AudioEffect? effect = AudioServer.GetBusEffect(0, i);
            if (effect is not null && effect.GetInstanceId() == id)
            {
                return i;
            }
        }

        return -1;
    }
}
