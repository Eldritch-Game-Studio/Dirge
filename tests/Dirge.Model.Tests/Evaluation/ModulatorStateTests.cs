using System;
using Dirge.Evaluation;
using Dirge.Project;
using NUnit.Framework;

namespace Dirge.Tests.Evaluation;

[TestFixture]
public sealed class DirgeRandomTests
{
    [Test]
    public void SameSeed_SameSequence()
    {
        var a = new DirgeRandom(42);
        var b = new DirgeRandom(42);
        for (int i = 0; i < 1000; i++) Assert.That(b.NextULong(), Is.EqualTo(a.NextULong()));
    }

    [Test]
    public void DifferentSeeds_DifferentSequences()
    {
        var a = new DirgeRandom(1);
        var b = new DirgeRandom(2);
        bool anyDifferent = false;
        for (int i = 0; i < 16; i++) anyDifferent |= a.NextULong() != b.NextULong();
        Assert.That(anyDifferent, Is.True);
    }

    [Test]
    public void SeedZero_IsValidAndDeterministic()
    {
        var a = new DirgeRandom(0);
        var b = new DirgeRandom(0);
        Assert.That(a.NextULong(), Is.Not.EqualTo(0UL));
        Assert.That(b.NextFloat(), Is.EqualTo(new DirgeRandom(0).NextFloat()));
    }

    [Test]
    public void NegativeSeed_IsDeterministic()
    {
        Assert.That(new DirgeRandom(-7).NextULong(), Is.EqualTo(new DirgeRandom(-7).NextULong()));
    }

    [Test]
    public void Ranges_AreRespected()
    {
        var r = new DirgeRandom(123);
        for (int i = 0; i < 10000; i++)
        {
            float f = r.NextFloat();
            Assert.That(f, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
            float s = r.NextSigned();
            Assert.That(s, Is.GreaterThanOrEqualTo(-1f).And.LessThanOrEqualTo(1f));
            int n = r.NextInt(7);
            Assert.That(n, Is.GreaterThanOrEqualTo(0).And.LessThan(7));
        }
        Assert.That(r.NextInt(0), Is.EqualTo(0));
        Assert.That(r.NextInt(-5), Is.EqualTo(0));
    }

    [Test]
    public void NextInt_CoversEveryBucket()
    {
        var r = new DirgeRandom(9);
        var seen = new bool[5];
        for (int i = 0; i < 1000; i++) seen[r.NextInt(5)] = true;
        Assert.That(seen, Is.All.True);
    }

    [Test]
    public void Chance_EdgesDoNotConsumeRandomness()
    {
        var r = new DirgeRandom(5);
        ulong before = new DirgeRandom(5).NextULong();
        Assert.That(r.Chance(1f), Is.True);
        Assert.That(r.Chance(1.5f), Is.True);
        Assert.That(r.Chance(0f), Is.False);
        Assert.That(r.Chance(-1f), Is.False);
        Assert.That(r.NextULong(), Is.EqualTo(before), "edge probabilities must not advance the stream");
    }

    [Test]
    public void Chance_HalfIsRoughlyHalf()
    {
        var r = new DirgeRandom(77);
        int hits = 0;
        for (int i = 0; i < 10000; i++) if (r.Chance(0.5f)) hits++;
        Assert.That(hits, Is.InRange(4600, 5400));
    }
}

[TestFixture]
public sealed class AhdsrStateTests
{
    private static Ahdsr Envelope() => new() { Attack = 1f, Hold = 0.5f, Decay = 1f, Sustain = 0.5f, Release = 2f };

    [Test]
    public void Stages_InOrder()
    {
        var s = new AhdsrState(Envelope());
        Assert.That(s.Current, Is.EqualTo(0f));
        Assert.That(s.Advance(0.5), Is.EqualTo(0.5f).Within(1e-6f), "attack midpoint");
        Assert.That(s.Advance(0.5), Is.EqualTo(1f).Within(1e-6f), "attack end / hold start");
        Assert.That(s.Advance(0.25), Is.EqualTo(1f).Within(1e-6f), "hold");
        Assert.That(s.Advance(0.25), Is.EqualTo(1f).Within(1e-6f), "decay start");
        Assert.That(s.Advance(0.5), Is.EqualTo(0.75f).Within(1e-6f), "decay midpoint");
        Assert.That(s.Advance(0.5), Is.EqualTo(0.5f).Within(1e-6f), "sustain reached");
        Assert.That(s.Advance(10), Is.EqualTo(0.5f).Within(1e-6f), "sustain holds indefinitely");
        Assert.That(s.IsReleased, Is.False);
        Assert.That(s.IsFinished, Is.False);
    }

    [Test]
    public void Release_FromSustain()
    {
        var s = new AhdsrState(Envelope());
        s.Advance(5);
        s.Release();
        Assert.That(s.IsReleased, Is.True);
        Assert.That(s.Current, Is.EqualTo(0.5f).Within(1e-6f));
        Assert.That(s.Advance(1), Is.EqualTo(0.25f).Within(1e-6f));
        Assert.That(s.IsFinished, Is.False);
        Assert.That(s.Advance(1), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(s.IsFinished, Is.True);
        Assert.That(s.Advance(1), Is.EqualTo(0f));
    }

    [Test]
    public void Release_FromPartialAttack_StartsAtCurrentLevel()
    {
        var s = new AhdsrState(Envelope());
        s.Advance(0.3);
        Assert.That(s.Current, Is.EqualTo(0.3f).Within(1e-6f));
        s.Release();
        Assert.That(s.Current, Is.EqualTo(0.3f).Within(1e-6f), "no jump at release");
        Assert.That(s.Advance(1), Is.EqualTo(0.15f).Within(1e-6f));
        Assert.That(s.Advance(1.5), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(s.IsFinished, Is.True);
    }

    [Test]
    public void Release_Twice_IsIdempotent()
    {
        var s = new AhdsrState(Envelope());
        s.Advance(0.5);
        s.Release();
        s.Advance(1);
        float level = s.Current;
        s.Release();
        Assert.That(s.Current, Is.EqualTo(level));
    }

    [Test]
    public void ZeroAttack_StartsAtFull()
    {
        var s = new AhdsrState(new Ahdsr { Attack = 0f, Hold = 0f, Decay = 0f, Sustain = 0.7f, Release = 1f });
        Assert.That(s.Current, Is.EqualTo(0.7f));
        var full = new AhdsrState(new Ahdsr());
        Assert.That(full.Current, Is.EqualTo(1f));
    }

    [Test]
    public void ZeroRelease_FinishesImmediately()
    {
        var s = new AhdsrState(new Ahdsr { Attack = 1f, Release = 0f });
        s.Advance(0.5);
        s.Release();
        Assert.That(s.Current, Is.EqualTo(0f));
        Assert.That(s.IsFinished, Is.True);
    }
}

[TestFixture]
public sealed class LfoStateTests
{
    private static LfoState At(LfoShape shape, float phase) => new(new LfoModulator { Shape = shape, Phase = phase }, null);

    [TestCase(LfoShape.Sine, 0f, 0f)]
    [TestCase(LfoShape.Sine, 0.25f, 1f)]
    [TestCase(LfoShape.Sine, 0.5f, 0f)]
    [TestCase(LfoShape.Sine, 0.75f, -1f)]
    [TestCase(LfoShape.Square, 0f, 1f)]
    [TestCase(LfoShape.Square, 0.49f, 1f)]
    [TestCase(LfoShape.Square, 0.5f, -1f)]
    [TestCase(LfoShape.Square, 0.99f, -1f)]
    [TestCase(LfoShape.Triangle, 0f, -1f)]
    [TestCase(LfoShape.Triangle, 0.25f, 0f)]
    [TestCase(LfoShape.Triangle, 0.5f, 1f)]
    [TestCase(LfoShape.Triangle, 0.75f, 0f)]
    [TestCase(LfoShape.SawUp, 0f, -1f)]
    [TestCase(LfoShape.SawUp, 0.5f, 0f)]
    [TestCase(LfoShape.SawUp, 0.75f, 0.5f)]
    [TestCase(LfoShape.SawDown, 0f, 1f)]
    [TestCase(LfoShape.SawDown, 0.5f, 0f)]
    [TestCase(LfoShape.SawDown, 0.75f, -0.5f)]
    public void Shapes_AtKnownPhases(LfoShape shape, float phase, float expected)
    {
        Assert.That(At(shape, phase).Current, Is.EqualTo(expected).Within(1e-5f));
    }

    [Test]
    public void Advance_AccumulatesPhaseByRate()
    {
        var s = At(LfoShape.SawUp, 0f);
        Assert.That(s.Advance(0.25, 1f), Is.EqualTo(-0.5f).Within(1e-5f));
        Assert.That(s.Advance(0.25, 2f), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(s.Advance(0.125, 2f), Is.EqualTo(-1f).Within(1e-5f), "wraps to phase 0");
    }

    [Test]
    public void Noise_WithoutRandom_IsZero()
    {
        var s = At(LfoShape.Noise, 0f);
        Assert.That(s.Current, Is.EqualTo(0f));
        Assert.That(s.Advance(1.5, 1f), Is.EqualTo(0f));
    }

    [Test]
    public void Noise_HoldsPerCycle_AndIsDeterministic()
    {
        var a = new LfoState(new LfoModulator { Shape = LfoShape.Noise }, new DirgeRandom(3));
        var b = new LfoState(new LfoModulator { Shape = LfoShape.Noise }, new DirgeRandom(3));
        float first = a.Current;
        Assert.That(first, Is.EqualTo(b.Current));
        Assert.That(a.Advance(0.5, 1f), Is.EqualTo(first), "same cycle: held");
        Assert.That(a.Advance(0.75, 1f), Is.Not.EqualTo(first), "wrapped: new sample");
        Assert.That(Math.Abs(a.Current), Is.LessThanOrEqualTo(1f));
        b.Advance(0.5, 1f);
        Assert.That(b.Advance(0.75, 1f), Is.EqualTo(a.Current));
    }
}
