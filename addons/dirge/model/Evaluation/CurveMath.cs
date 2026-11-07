using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Evaluation;

public static class CurveMath
{
    /// <summary>Evaluate a curve at <paramref name="x"/>. Empty curve → 0; clamps outside the first/last point.</summary>
    public static float Evaluate(Curve curve, float x)
    {
        List<CurvePoint> points = curve.Points;
        int count = points.Count;
        if (count == 0) return 0f;
        if (x <= points[0].X) return points[0].Y;
        if (x >= points[count - 1].X) return points[count - 1].Y;
        int hi = 1;
        while (points[hi].X < x) hi++;
        CurvePoint a = points[hi - 1];
        CurvePoint b = points[hi];
        float span = b.X - a.X;
        float t = span <= 0f ? 1f : (x - a.X) / span;
        switch (a.ShapeToNext)
        {
            case CurveShape.Stepped: return a.Y;
            case CurveShape.Smooth: t = t * t * (3f - 2f * t); break;
        }
        return a.Y + (b.Y - a.Y) * t;
    }

    public static float DbToLinear(float db) => db <= -80f ? 0f : MathF.Pow(10f, db / 20f);
    public static float LinearToDb(float linear) => linear <= 1e-4f ? -80f : 20f * MathF.Log10(linear);
}
