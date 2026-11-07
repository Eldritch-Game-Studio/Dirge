using System;
using Dirge.Metering;
using NUnit.Framework;

namespace Dirge.Tests.Metering;

[TestFixture]
public sealed class LoudnessMeterTests
{
    private static float[] Sine(int sampleRate, int channels, double seconds, double frequency, double dbfs, int onlyChannel = -1)
    {
        int frames = (int)(sampleRate * seconds);
        var buffer = new float[frames * channels];
        double amplitude = Math.Pow(10.0, dbfs / 20.0);
        for (int f = 0; f < frames; f++)
        {
            float s = (float)(amplitude * Math.Sin(2.0 * Math.PI * frequency * f / sampleRate));
            for (int c = 0; c < channels; c++)
                buffer[f * channels + c] = onlyChannel < 0 || c == onlyChannel ? s : 0f;
        }
        return buffer;
    }

    private static void PushInChunks(LoudnessMeter meter, float[] data, int chunkFrames)
    {
        int chunk = chunkFrames * meter.Channels;
        for (int i = 0; i < data.Length; i += chunk)
            meter.Push(new ReadOnlySpan<float>(data, i, Math.Min(chunk, data.Length - i)));
    }

    [TestCase(48000)]
    [TestCase(44100)]
    public void StereoSine_Minus23dBFS_1kHz_ReadsMinus23Lufs(int sampleRate)
    {
        var meter = new LoudnessMeter(sampleRate, 2);
        PushInChunks(meter, Sine(sampleRate, 2, 10.0, 1000.0, -23.0), 512);
        Assert.That(meter.IntegratedLufs, Is.EqualTo(-23f).Within(0.1f));
        Assert.That(meter.MomentaryLufs, Is.EqualTo(-23f).Within(0.1f));
        Assert.That(meter.ShortTermLufs, Is.EqualTo(-23f).Within(0.1f));
    }

    [Test]
    public void MonoSine_0dBFS_1kHz_ReadsMinus3_01Lufs()
    {
        // BS.1770-4: a 0 dBFS 1 kHz sine on one of L/C/R indicates −3.01 LKFS.
        var meter = new LoudnessMeter(48000, 1);
        meter.Push(Sine(48000, 1, 5.0, 1000.0, 0.0));
        Assert.That(meter.IntegratedLufs, Is.EqualTo(-3.01f).Within(0.1f));
    }

    [Test]
    public void SurroundWeights_RearChannelIsLouderThanFront()
    {
        var front = new LoudnessMeter(48000, 6);
        front.Push(Sine(48000, 6, 3.0, 1000.0, -20.0, onlyChannel: 0));
        var rear = new LoudnessMeter(48000, 6);
        rear.Push(Sine(48000, 6, 3.0, 1000.0, -20.0, onlyChannel: 4));
        var lfe = new LoudnessMeter(48000, 6);
        lfe.Push(Sine(48000, 6, 3.0, 1000.0, -20.0, onlyChannel: 3));
        Assert.That(rear.IntegratedLufs - front.IntegratedLufs, Is.EqualTo(10.0 * Math.Log10(1.41)).Within(0.05));
        Assert.That(lfe.IntegratedLufs, Is.EqualTo(float.NegativeInfinity), "LFE does not contribute");
    }

    [Test]
    public void Integrated_GatesOutSilence_AndQuietPassages()
    {
        // Blocks straddling a level change are legitimately gated in, so use long passages (EBU Tech 3341 style) to keep their share small.
        var meter = new LoudnessMeter(48000, 2);
        PushInChunks(meter, Sine(48000, 2, 20.0, 1000.0, -23.0), 1024);
        PushInChunks(meter, new float[48000 * 2 * 10], 1024);                 // 10 s digital silence → absolute gate
        PushInChunks(meter, Sine(48000, 2, 10.0, 1000.0, -45.0), 1024);        // 10 s at −45 → relative gate (≈ −36 threshold)
        Assert.That(meter.IntegratedLufs, Is.EqualTo(-23f).Within(0.1f));
        Assert.That(meter.MomentaryLufs, Is.EqualTo(-45f).Within(0.2f), "momentary follows the quiet tail");

        var silent = new LoudnessMeter(48000, 2);
        silent.Push(new float[48000 * 2]);
        Assert.That(silent.IntegratedLufs, Is.EqualTo(float.NegativeInfinity));
        Assert.That(silent.MomentaryLufs, Is.EqualTo(float.NegativeInfinity));
    }

    [Test]
    public void KWeighting_AttenuatesLowFrequencies_AndBoostsHigh()
    {
        // K-filter gains (48 kHz): −5.61 dB at 40 Hz, +0.65 dB at 1 kHz (cancelled by the −0.691 offset), +4.00 dB at 8 kHz.
        var low = new LoudnessMeter(48000, 2);
        low.Push(Sine(48000, 2, 4.0, 40.0, -23.0));
        var high = new LoudnessMeter(48000, 2);
        high.Push(Sine(48000, 2, 4.0, 8000.0, -23.0));
        Assert.That(low.IntegratedLufs, Is.EqualTo(-23f - 5.61f - 0.691f).Within(0.15f), "RLB high-pass attenuates 40 Hz");
        Assert.That(high.IntegratedLufs, Is.EqualTo(-23f + 4.0f - 0.691f).Within(0.1f), "stage-1 shelf adds +4 dB at 8 kHz");
    }

    [TestCase(48000)]
    [TestCase(44100)]
    public void TruePeak_FullScaleSine_BetweenZeroAndPlus0_2dBTP(int sampleRate)
    {
        var meter = new LoudnessMeter(sampleRate, 2);
        meter.Push(Sine(sampleRate, 2, 1.0, 997.0, 0.0));
        Assert.That(meter.TruePeakDbtp, Is.GreaterThanOrEqualTo(-0.01f).And.LessThanOrEqualTo(0.2f));
    }

    [Test]
    public void TruePeak_SeesInterSamplePeaks()
    {
        // A quarter-sample-rate sine sampled at 45° never hits a sample peak (|s| = 0.7071) but its true peak is 1.0.
        var meter = new LoudnessMeter(48000, 1);
        int frames = 4800;
        var buffer = new float[frames];
        for (int i = 0; i < frames; i++) buffer[i] = (float)Math.Sin(Math.PI / 2 * i + Math.PI / 4);
        meter.Push(buffer);
        Assert.That(meter.TruePeakDbtp, Is.GreaterThan(-0.6f).And.LessThan(0.2f), "sample peak would read −3 dBFS");
    }

    [Test]
    public void Reset_ClearsEverything()
    {
        var meter = new LoudnessMeter(48000, 2);
        meter.Push(Sine(48000, 2, 2.0, 1000.0, -6.0));
        Assert.That(meter.IntegratedLufs, Is.GreaterThan(-10f));
        meter.Reset();
        Assert.That(meter.IntegratedLufs, Is.EqualTo(float.NegativeInfinity));
        Assert.That(meter.MomentaryLufs, Is.EqualTo(float.NegativeInfinity));
        Assert.That(meter.ShortTermLufs, Is.EqualTo(float.NegativeInfinity));
        Assert.That(meter.TruePeakDbtp, Is.EqualTo(float.NegativeInfinity));
    }

    [Test]
    public void Constructor_RejectsUnsupportedLayouts()
    {
        System.Action badChannels = () => _ = new LoudnessMeter(48000, 3);
        Assert.Throws<ArgumentOutOfRangeException>(badChannels);
        System.Action badRate = () => _ = new LoudnessMeter(1000, 2);
        Assert.Throws<ArgumentOutOfRangeException>(badRate);
    }
}
