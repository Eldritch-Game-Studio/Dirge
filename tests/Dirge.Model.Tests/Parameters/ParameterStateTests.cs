using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Parameters;

[TestFixture]
public sealed class ParameterStateTests
{
    private static ParameterDefinition Seeking(float up, float down) => new()
    {
        Name = "P", Minimum = 0f, Maximum = 10f, Default = 0f, SeekSpeedUp = up, SeekSpeedDown = down,
    };

    [Test]
    public void StartsAtDefault()
    {
        var s = new ParameterState(new ParameterDefinition { Minimum = 0f, Maximum = 1f, Default = 0.25f });
        Assert.That(s.Value, Is.EqualTo(0.25f));
        Assert.That(s.Target, Is.EqualTo(0.25f));
        Assert.That(s.Advance(1), Is.False);
    }

    [Test]
    public void InstantSeek_WhenSpeedIsZero()
    {
        var s = new ParameterState(Seeking(0f, 0f));
        s.Set(7f);
        Assert.That(s.Value, Is.EqualTo(0f), "Set is not immediate by default");
        Assert.That(s.Advance(0.001), Is.True);
        Assert.That(s.Value, Is.EqualTo(7f));
        Assert.That(s.Advance(1), Is.False);
    }

    [Test]
    public void Immediate_SkipsSeek()
    {
        var s = new ParameterState(Seeking(1f, 1f));
        s.Set(7f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(7f));
        Assert.That(s.Advance(1), Is.False);
    }

    [Test]
    public void SeekUp_UsesUpSpeed_AndStopsExactlyAtTarget()
    {
        var s = new ParameterState(Seeking(up: 2f, down: 1f));
        s.Set(5f);
        Assert.That(s.Advance(1), Is.True);
        Assert.That(s.Value, Is.EqualTo(2f).Within(1e-6f));
        s.Advance(1);
        Assert.That(s.Value, Is.EqualTo(4f).Within(1e-6f));
        s.Advance(1);
        Assert.That(s.Value, Is.EqualTo(5f), "clamped to target, no overshoot");
        Assert.That(s.Advance(1), Is.False);
    }

    [Test]
    public void SeekDown_UsesDownSpeed()
    {
        var s = new ParameterState(Seeking(up: 2f, down: 1f));
        s.Set(5f, immediate: true);
        s.Set(0f);
        s.Advance(1);
        Assert.That(s.Value, Is.EqualTo(4f).Within(1e-6f), "down speed is 1/s, not 2/s");
        s.Advance(10);
        Assert.That(s.Value, Is.EqualTo(0f));
    }

    [Test]
    public void Asymmetry_UpAndDownDifferInTimeToReach()
    {
        var up = new ParameterState(Seeking(up: 4f, down: 1f));
        up.Set(4f);
        up.Advance(1);
        Assert.That(up.Value, Is.EqualTo(4f), "4 units/s up reaches in 1s");

        var down = new ParameterState(Seeking(up: 4f, down: 1f));
        down.Set(4f, immediate: true);
        down.Set(0f);
        down.Advance(1);
        Assert.That(down.Value, Is.EqualTo(3f).Within(1e-6f), "1 unit/s down takes 4s");
    }

    [Test]
    public void RetargetMidSeek_ReversesDirectionWithNewSpeed()
    {
        var s = new ParameterState(Seeking(up: 2f, down: 0.5f));
        s.Set(10f);
        s.Advance(1); // 2
        s.Set(0f);
        s.Advance(1); // 1.5
        Assert.That(s.Value, Is.EqualTo(1.5f).Within(1e-6f));
    }

    [Test]
    public void OnlyOneSideInstant()
    {
        var s = new ParameterState(Seeking(up: 0f, down: 1f));
        s.Set(10f);
        s.Advance(0.01);
        Assert.That(s.Value, Is.EqualTo(10f), "up is instant");
        s.Set(9f);
        s.Advance(0.5);
        Assert.That(s.Value, Is.EqualTo(9.5f).Within(1e-6f), "down seeks");
    }

    [Test]
    public void Set_ClampsToRange()
    {
        var s = new ParameterState(Seeking(0f, 0f));
        s.Set(50f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(10f));
        s.Set(-50f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(0f));
    }

    [TestCase(ParameterKind.Discrete)]
    [TestCase(ParameterKind.Labeled)]
    public void NonContinuous_RoundsTarget(ParameterKind kind)
    {
        var s = new ParameterState(new ParameterDefinition { Kind = kind, Minimum = 0f, Maximum = 5f });
        s.Set(2.4f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(2f));
        s.Set(2.6f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(3f));
        s.Set(4.9f);
        Assert.That(s.Target, Is.EqualTo(5f));
        s.Set(9f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(5f), "clamped before rounding");
    }

    [Test]
    public void Continuous_DoesNotRound()
    {
        var s = new ParameterState(Seeking(0f, 0f));
        s.Set(2.4f, immediate: true);
        Assert.That(s.Value, Is.EqualTo(2.4f));
    }

    [Test]
    public void Bank_ResolvesByIdAndName_AndAdvancesAll()
    {
        ParameterDefinition a = Seeking(1f, 1f);
        a.Name = "A";
        ParameterDefinition b = Seeking(0f, 0f);
        b.Name = "B";
        var bank = new ParameterBank(new[] { a, b });
        Assert.That(bank.All, Has.Count.EqualTo(2));
        Assert.That(bank.TryGet("A", out ParameterState? byName), Is.True);
        Assert.That(bank.TryGet(a.Id, out ParameterState? byId), Is.True);
        Assert.That(byName, Is.SameAs(byId));
        Assert.That(bank.TryGet("Missing", out _), Is.False);
        Assert.That(bank.TryGet(DirgeId.New(), out _), Is.False);

        Assert.That(bank.Advance(1), Is.False);
        byName!.Set(2f);
        Assert.That(bank.Advance(1), Is.True);
        Assert.That(byName.Value, Is.EqualTo(1f).Within(1e-6f));
        Assert.That(bank.Advance(1), Is.True);
        Assert.That(bank.Advance(1), Is.False);
    }
}
