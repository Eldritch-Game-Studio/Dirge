using Dirge.Evaluation;
using Dirge.Project;
using NUnit.Framework;

namespace Dirge.Tests.Evaluation;

[TestFixture]
public sealed class CurveMathTests
{
    private static Curve Curve(params CurvePoint[] points)
    {
        var curve = new Curve();
        curve.Points.AddRange(points);
        return curve;
    }

    [Test]
    public void Evaluate_EmptyCurve_IsZero()
    {
        Assert.That(CurveMath.Evaluate(new Curve(), 0.5f), Is.EqualTo(0f));
    }

    [Test]
    public void Evaluate_SinglePoint_IsConstant()
    {
        Curve c = Curve(new CurvePoint(0.5f, 7f));
        Assert.That(CurveMath.Evaluate(c, -10f), Is.EqualTo(7f));
        Assert.That(CurveMath.Evaluate(c, 0.5f), Is.EqualTo(7f));
        Assert.That(CurveMath.Evaluate(c, 10f), Is.EqualTo(7f));
    }

    [Test]
    public void Evaluate_ClampsOutsideRange()
    {
        Curve c = Curve(new CurvePoint(0f, -20f), new CurvePoint(1f, 0f));
        Assert.That(CurveMath.Evaluate(c, -1f), Is.EqualTo(-20f));
        Assert.That(CurveMath.Evaluate(c, 0f), Is.EqualTo(-20f));
        Assert.That(CurveMath.Evaluate(c, 1f), Is.EqualTo(0f));
        Assert.That(CurveMath.Evaluate(c, 2f), Is.EqualTo(0f));
    }

    [Test]
    public void Evaluate_Linear()
    {
        Curve c = Curve(new CurvePoint(0f, 0f), new CurvePoint(2f, 10f), new CurvePoint(4f, 0f));
        Assert.That(CurveMath.Evaluate(c, 0.5f), Is.EqualTo(2.5f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 1f), Is.EqualTo(5f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 2f), Is.EqualTo(10f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 3f), Is.EqualTo(5f).Within(1e-5f));
    }

    [Test]
    public void Evaluate_Smooth_IsSmoothstep()
    {
        Curve c = Curve(new CurvePoint(0f, 0f, CurveShape.Smooth), new CurvePoint(1f, 1f));
        Assert.That(CurveMath.Evaluate(c, 0.5f), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 0.25f), Is.EqualTo(0.15625f).Within(1e-5f)); // 3t² − 2t³ at t = 0.25
        Assert.That(CurveMath.Evaluate(c, 0.75f), Is.EqualTo(0.84375f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 0.25f), Is.LessThan(0.25f));
        Assert.That(CurveMath.Evaluate(c, 0.75f), Is.GreaterThan(0.75f));
    }

    [Test]
    public void Evaluate_Stepped_HoldsThroughNextPointX()
    {
        Curve c = Curve(new CurvePoint(0f, 1f, CurveShape.Stepped), new CurvePoint(1f, 5f), new CurvePoint(2f, 9f));
        Assert.That(CurveMath.Evaluate(c, 0.01f), Is.EqualTo(1f));
        Assert.That(CurveMath.Evaluate(c, 0.99f), Is.EqualTo(1f));
        Assert.That(CurveMath.Evaluate(c, 1f), Is.EqualTo(1f), "the step is closed at the segment end: exactly X=1 still resolves the stepped segment");
        Assert.That(CurveMath.Evaluate(c, 1.001f), Is.EqualTo(5f).Within(1e-2f));
        Assert.That(CurveMath.Evaluate(c, 1.5f), Is.EqualTo(7f).Within(1e-5f)); // second segment is linear
    }

    [Test]
    public void Evaluate_ShapeIsPerSegment()
    {
        Curve c = Curve(new CurvePoint(0f, 0f, CurveShape.Linear), new CurvePoint(1f, 1f, CurveShape.Stepped), new CurvePoint(2f, 2f));
        Assert.That(CurveMath.Evaluate(c, 0.5f), Is.EqualTo(0.5f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 1.5f), Is.EqualTo(1f));
    }

    [Test]
    public void Evaluate_DegenerateSpan_FirstDuplicateWinsAtX_SecondGovernsNextSegment()
    {
        // Two points share X = 1: exactly at 1 the segment ending there resolves (10); past 1 the later point is the segment start.
        Curve c = Curve(new CurvePoint(0f, 0f), new CurvePoint(1f, 10f), new CurvePoint(1f, 20f), new CurvePoint(2f, 30f));
        Assert.That(CurveMath.Evaluate(c, 1f), Is.EqualTo(10f));
        Assert.That(CurveMath.Evaluate(c, 0.5f), Is.EqualTo(5f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 1.5f), Is.EqualTo(25f).Within(1e-5f));
        Assert.That(CurveMath.Evaluate(c, 1.0001f), Is.EqualTo(20f).Within(1e-2f));
    }

    [Test]
    public void Evaluate_DegenerateSpan_Stepped_StillHoldsFirst()
    {
        Curve c = Curve(new CurvePoint(1f, 10f, CurveShape.Stepped), new CurvePoint(1f, 20f));
        Assert.That(CurveMath.Evaluate(c, 1f), Is.EqualTo(10f));
    }

    [Test]
    public void DbLinear_RoundTripsAndFloors()
    {
        Assert.That(CurveMath.DbToLinear(0f), Is.EqualTo(1f).Within(1e-6f));
        Assert.That(CurveMath.DbToLinear(-6.0206f), Is.EqualTo(0.5f).Within(1e-4f));
        Assert.That(CurveMath.DbToLinear(-80f), Is.EqualTo(0f));
        Assert.That(CurveMath.DbToLinear(-100f), Is.EqualTo(0f));
        Assert.That(CurveMath.LinearToDb(1f), Is.EqualTo(0f).Within(1e-6f));
        Assert.That(CurveMath.LinearToDb(0f), Is.EqualTo(-80f));
        Assert.That(CurveMath.LinearToDb(CurveMath.DbToLinear(-12f)), Is.EqualTo(-12f).Within(1e-4f));
    }
}
