#nullable enable
using System;
using System.Numerics;
using Dirge.Project;
using Dirge.Runtime;

namespace Dirge.Tool.Transport;

/// <summary>
/// Built-in parameter values the transport displays for the preview emitter/listener pair. Mirrors the model's internal
/// spatial math (<c>Dirge.Runtime.Spatial</c> is not public): degrees, yaw positive towards the listener's right.
/// </summary>
public readonly record struct PreviewReadout(float Distance, float DistanceNormalized, float Direction, float Elevation, float ConeAngle, float Orientation, float Speed, float SpeedAbsolute)
{
    private const float Epsilon = 1e-6f;
    private const float RadToDeg = 180f / MathF.PI;

    public static PreviewReadout Compute(in Attributes3D emitter, in Attributes3D listener, float minDistance, float maxDistance)
    {
        Vector3 toEmitter = emitter.Position - listener.Position;
        float distance = toEmitter.Length();
        float range = maxDistance - minDistance;
        float normalized = range <= Epsilon ? (distance >= maxDistance ? 1f : 0f) : Math.Clamp((distance - minDistance) / range, 0f, 1f);
        float elevation = 0f;
        float upLen = listener.Up.Length();
        if (distance > Epsilon && upLen > Epsilon)
        {
            float s = Vector3.Dot(toEmitter, listener.Up) / (distance * upLen);
            elevation = MathF.Asin(Math.Clamp(s, -1f, 1f)) * RadToDeg;
        }
        float cone = 0f;
        float emitterForwardLen = emitter.Forward.Length();
        if (distance > Epsilon && emitterForwardLen > Epsilon)
        {
            float c = Vector3.Dot(emitter.Forward, -toEmitter) / (distance * emitterForwardLen);
            cone = MathF.Acos(Math.Clamp(c, -1f, 1f)) * RadToDeg;
        }
        float speed = 0f;
        if (distance > Epsilon)
        {
            Vector3 relative = emitter.Velocity - listener.Velocity;
            speed = Vector3.Dot(relative, toEmitter) / distance;
        }
        return new PreviewReadout(
            distance,
            normalized,
            SignedYaw(listener.Forward, listener.Up, toEmitter),
            elevation,
            cone,
            SignedYaw(listener.Forward, listener.Up, emitter.Forward),
            speed,
            emitter.Velocity.Length());
    }

    /// <summary>Signed angle of <paramref name="v"/> relative to <paramref name="forward"/> in the plane perpendicular to <paramref name="up"/>, in (−180, 180].</summary>
    public static float SignedYaw(Vector3 forward, Vector3 up, Vector3 v)
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

    /// <summary>Rotates a horizontal direction about +Y by <paramref name="degrees"/> (right-handed: positive turns −Z towards −X, counter-clockwise seen from above).</summary>
    public static Vector3 RotateYaw(Vector3 direction, float degrees)
    {
        float r = degrees / RadToDeg;
        float c = MathF.Cos(r), s = MathF.Sin(r);
        return new Vector3(direction.X * c + direction.Z * s, direction.Y, -direction.X * s + direction.Z * c);
    }

    /// <summary>Value of one built-in parameter from this readout.</summary>
    public float Get(BuiltInParameter kind) => kind switch
    {
        BuiltInParameter.Distance => Distance,
        BuiltInParameter.DistanceNormalized => DistanceNormalized,
        BuiltInParameter.Direction => Direction,
        BuiltInParameter.Elevation => Elevation,
        BuiltInParameter.EventConeAngle => ConeAngle,
        BuiltInParameter.EventOrientation => Orientation,
        BuiltInParameter.Speed => Speed,
        BuiltInParameter.SpeedAbsolute => SpeedAbsolute,
        _ => 0f,
    };
}
