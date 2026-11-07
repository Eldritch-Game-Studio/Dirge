using System;
using System.Numerics;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>
/// Built-in parameter math: everything derived from an emitter's and a listener's <see cref="Attributes3D"/>.
/// Angles are degrees. Yaw-style angles are measured in the listener's horizontal plane (perpendicular to its Up),
/// positive towards the listener's right (<c>cross(Forward, Up)</c>; +X for the default −Z forward / +Y up frame).
/// </summary>
internal static class Spatial
{
    private const float Epsilon = 1e-6f;
    private const float RadToDeg = 180f / MathF.PI;

    public static float Compute(BuiltInParameter kind, in Attributes3D emitter, in Attributes3D listener, float minDistance, float maxDistance)
    {
        Vector3 toEmitter = emitter.Position - listener.Position;
        switch (kind)
        {
            case BuiltInParameter.Distance:
                return toEmitter.Length();
            case BuiltInParameter.DistanceNormalized:
            {
                float d = toEmitter.Length();
                float range = maxDistance - minDistance;
                if (range <= Epsilon) return d >= maxDistance ? 1f : 0f;
                return Math.Clamp((d - minDistance) / range, 0f, 1f);
            }
            case BuiltInParameter.Direction:
                return SignedYaw(listener.Forward, listener.Up, toEmitter);
            case BuiltInParameter.Elevation:
            {
                float len = toEmitter.Length();
                float upLen = listener.Up.Length();
                if (len <= Epsilon || upLen <= Epsilon) return 0f;
                float s = Vector3.Dot(toEmitter, listener.Up) / (len * upLen);
                return MathF.Asin(Math.Clamp(s, -1f, 1f)) * RadToDeg;
            }
            case BuiltInParameter.EventConeAngle:
            {
                float len = toEmitter.Length();
                float fLen = emitter.Forward.Length();
                if (len <= Epsilon || fLen <= Epsilon) return 0f;
                // Angle between the emitter's forward and the direction from the emitter to the listener.
                float c = Vector3.Dot(emitter.Forward, -toEmitter) / (len * fLen);
                return MathF.Acos(Math.Clamp(c, -1f, 1f)) * RadToDeg;
            }
            case BuiltInParameter.EventOrientation:
                return SignedYaw(listener.Forward, listener.Up, emitter.Forward);
            case BuiltInParameter.Speed:
            {
                // Relative speed along the listener→emitter axis; positive = receding, negative = approaching.
                float len = toEmitter.Length();
                if (len <= Epsilon) return 0f;
                Vector3 relative = emitter.Velocity - listener.Velocity;
                return Vector3.Dot(relative, toEmitter) / len;
            }
            case BuiltInParameter.SpeedAbsolute:
                return emitter.Velocity.Length();
            default:
                return 0f;
        }
    }

    /// <summary>Signed angle of <paramref name="v"/> relative to <paramref name="forward"/> in the plane perpendicular to <paramref name="up"/>, in (−180, 180].</summary>
    private static float SignedYaw(Vector3 forward, Vector3 up, Vector3 v)
    {
        float upLen = up.Length();
        if (upLen <= Epsilon) return 0f;
        up /= upLen;
        Vector3 f = forward - up * Vector3.Dot(forward, up);
        float fLen = f.Length();
        if (fLen <= Epsilon) return 0f;
        f /= fLen;
        Vector3 right = Vector3.Cross(f, up);
        Vector3 p = v - up * Vector3.Dot(v, up);
        if (p.LengthSquared() <= Epsilon * Epsilon) return 0f;
        return MathF.Atan2(Vector3.Dot(p, right), Vector3.Dot(p, f)) * RadToDeg;
    }
}
