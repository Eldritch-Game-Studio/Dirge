using Dirge.Evaluation;
using Dirge.Project;
using NUnit.Framework;

namespace Dirge.Tests.Evaluation;

[TestFixture]
public sealed class AutopitchMathTests
{
    private const float Tolerance = 1e-6f;

    private static AutopitchModulator Rpm() => new() { ReferenceValue = 1000f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f, Amount = 1f };

    [Test]
    public void Ratio_IsOneAtReference_AndEndpointsAtRange()
    {
        AutopitchModulator m = Rpm();
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 1000f), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 0f), Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 4000f), Is.EqualTo(2f).Within(Tolerance));
    }

    [Test]
    public void Ratio_InterpolatesLinearlyOnEachSide()
    {
        AutopitchModulator m = Rpm();
        // Below reference: half-way between min (0.5) and reference (1).
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 500f), Is.EqualTo(0.75f).Within(Tolerance));
        // Above reference: a quarter of the way between reference (1) and max (2).
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 1750f), Is.EqualTo(1.25f).Within(Tolerance));
    }

    [Test]
    public void Ratio_ClampsOutsideRange()
    {
        AutopitchModulator m = Rpm();
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, -50f), Is.EqualTo(0.5f).Within(Tolerance));
        Assert.That(AutopitchMath.Ratio(m, 0f, 4000f, 9000f), Is.EqualTo(2f).Within(Tolerance));
    }

    [Test]
    public void Ratio_ReferenceAtEndpoint_CollapsesThatSideToOne()
    {
        var m = new AutopitchModulator { ReferenceValue = 0f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f };
        Assert.That(AutopitchMath.Ratio(m, 0f, 1f, 0f), Is.EqualTo(1f).Within(Tolerance));
        Assert.That(AutopitchMath.Ratio(m, 0f, 1f, 0.5f), Is.EqualTo(1.5f).Within(Tolerance));
        Assert.That(AutopitchMath.Ratio(m, 0f, 1f, 1f), Is.EqualTo(2f).Within(Tolerance));
    }

    [Test]
    public void Evaluate_AmountScalesDeviationFromOne()
    {
        AutopitchModulator m = Rpm();
        m.Amount = 0.5f;
        Assert.That(AutopitchMath.Evaluate(m, 0f, 4000f, 4000f), Is.EqualTo(1.5f).Within(Tolerance), "half of +1");
        Assert.That(AutopitchMath.Evaluate(m, 0f, 4000f, 0f), Is.EqualTo(0.75f).Within(Tolerance), "half of -0.5");
        Assert.That(AutopitchMath.Evaluate(m, 0f, 4000f, 1000f), Is.EqualTo(1f).Within(Tolerance));
        m.Amount = 0f;
        Assert.That(AutopitchMath.Evaluate(m, 0f, 4000f, 4000f), Is.EqualTo(1f).Within(Tolerance));
    }
}
