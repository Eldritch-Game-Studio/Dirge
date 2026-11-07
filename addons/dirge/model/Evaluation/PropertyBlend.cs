using System;
using Dirge.Project;

namespace Dirge.Evaluation;

/// <summary>One snapshot's contribution to a single property.</summary>
public readonly record struct SnapshotContribution(int Priority, float Value, float Intensity, SnapshotMode Mode);

/// <summary>
/// Snapshot composition rule shared by the mixer and tests. Contributions are applied from the lowest priority to the
/// highest so the highest-priority snapshot has the final say:
/// <list type="bullet">
/// <item><see cref="SnapshotMode.Blending"/>: the running result is interpolated towards the snapshot value by intensity.</item>
/// <item><see cref="SnapshotMode.Overriding"/> with intensity &gt; 0: every lower-priority result is discarded and the authored
/// value is interpolated towards the snapshot value by intensity.</item>
/// </list>
/// Intensity ≤ 0 contributes nothing; intensity is clamped to 1.
/// </summary>
public static class PropertyBlend
{
    /// <summary>Apply one contribution on top of <paramref name="current"/>. Call in ascending priority order.</summary>
    public static float Apply(float current, float authored, float value, float intensity, SnapshotMode mode)
    {
        if (intensity <= 0f) return current;
        if (intensity > 1f) intensity = 1f;
        float from = mode == SnapshotMode.Overriding ? authored : current;
        return from + (value - from) * intensity;
    }

    /// <summary>Compose a set of contributions sorted by <see cref="SnapshotContribution.Priority"/> descending (highest first).</summary>
    public static float Compose(float authored, ReadOnlySpan<SnapshotContribution> sortedByPriorityDescending)
    {
        float result = authored;
        for (int i = sortedByPriorityDescending.Length - 1; i >= 0; i--)
        {
            SnapshotContribution c = sortedByPriorityDescending[i];
            result = Apply(result, authored, c.Value, c.Intensity, c.Mode);
        }
        return result;
    }
}
