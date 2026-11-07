using System;
using Dirge.Project;

namespace Dirge.Runtime;

internal enum PointKind { Marker, LoopEnd, RegionStart, RegionEnd }

/// <summary>A position on the timeline where a logic marker does something. Sorted by position, then kind.</summary>
internal readonly struct TimelinePoint
{
    public readonly double Position;
    public readonly LogicMarker Marker;
    public readonly PointKind Kind;

    public TimelinePoint(double position, LogicMarker marker, PointKind kind)
    {
        Position = position;
        Marker = marker;
        Kind = kind;
    }
}

/// <summary>
/// One instance's playhead. Advances in segments between logic points so loops, quantized transitions, sustain points
/// and beats are processed in order within a tick; the owning <see cref="EventInstance"/> is told about every segment
/// and jump so instruments can trigger, cut or re-seek.
/// </summary>
internal sealed class Timeline
{
    private const double Epsilon = 1e-9;
    private const int MaxJumpsPerTick = 64;

    private readonly EventInstance _instance;
    private readonly EventDescription _description;

    public double Position;
    /// <summary>Held at a <see cref="SustainPoint"/> until <see cref="KeyOff"/>.</summary>
    public bool Holding;

    private bool _inclusive;
    private bool _pendingJump;
    private double _pendingJumpAt;
    private double _pendingJumpTo;
    private TransitionRegion? _armed;
    private double _armedAt;
    private double _lastBeat;
    private bool _beatInclusive;
    private int _jumps;

    public Timeline(EventInstance instance, EventDescription description)
    {
        _instance = instance;
        _description = description;
    }

    /// <summary>Place the playhead without evaluating anything (stopped instances, seeks applied at the next update).</summary>
    public void Reset(double position)
    {
        Position = position;
        Holding = false;
        _inclusive = true;
        _pendingJump = false;
        _armed = null;
        _lastBeat = position;
        _beatInclusive = true;
        _jumps = 0;
    }

    /// <summary>Playback (re)starts at <see cref="Position"/>: fire the beat and arm a transition region under the playhead. Returns true if that caused a jump.</summary>
    public bool Begin()
    {
        _jumps = 0;
        ProcessBeats(Position);
        ArmRegionContaining(Position);
        return _jumps > 0;
    }

    public void KeyOff()
    {
        if (!Holding) return;
        Holding = false;
        _inclusive = false;
    }

    public void Advance(double dt)
    {
        if (Holding || dt <= 0) return;
        _jumps = 0;
        TimelinePoint[] points = _description.Points;
        double remaining = dt;
        int guard = 0;
        while (remaining > 0 && guard++ < 4096)
        {
            double target = Position + remaining;
            int pi = NextPointIndex(points, Position, _inclusive);
            double next = pi >= 0 ? points[pi].Position : double.PositiveInfinity;
            int kind = 0; // 0 = logic point, 1 = pending quantized jump, 2 = armed transition region check
            if (_pendingJump && _pendingJumpAt <= next) { next = _pendingJumpAt; kind = 1; }
            if (_armed is not null && _armedAt <= next) { next = _armedAt; kind = 2; }
            if (next > target)
            {
                MoveTo(target);
                return;
            }
            bool arrivedByJump = _inclusive && next <= Position + Epsilon;
            remaining -= Math.Max(0, next - Position);
            MoveTo(next);
            switch (kind)
            {
                case 1:
                    _pendingJump = false;
                    Jump(_pendingJumpTo);
                    break;
                case 2:
                {
                    TransitionRegion region = _armed!;
                    if (_instance.MarkerPasses(region) && _description.TryGetMarkerPosition(region.Destination, out double dest))
                    {
                        Jump(dest);
                    }
                    else if (!_description.NextGrid(Position, region.Quantization, false, out _armedAt))
                    {
                        _armed = null;
                    }
                    break;
                }
                default:
                    for (int j = pi; j < points.Length && points[j].Position <= next + Epsilon; j++)
                        if (HandlePoint(points[j], arrivedByJump)) break;
                    break;
            }
            if (Holding) return;
        }
    }

    private static int NextPointIndex(TimelinePoint[] points, double position, bool inclusive)
    {
        for (int i = 0; i < points.Length; i++)
        {
            double p = points[i].Position;
            if (inclusive ? p >= position - Epsilon : p > position + Epsilon) return i;
        }
        return -1;
    }

    private void MoveTo(double position)
    {
        double from = Position;
        bool inclusive = _inclusive;
        Position = position;
        _inclusive = false;
        ProcessBeats(position);
        _instance.OnTimelineSegment(from, position, inclusive);
    }

    private void ProcessBeats(double upTo)
    {
        int guard = 0;
        while (guard++ < 4096 && _description.NextBeat(_lastBeat, _beatInclusive, out double at, out int bar, out int beat, out float bpm))
        {
            if (at > upTo + Epsilon) break;
            _lastBeat = at;
            _beatInclusive = false;
            _instance.OnBeat(bar, beat, bpm, at);
        }
    }

    /// <returns>true when the point ended this segment (jump or hold); remaining points at the same position are skipped.</returns>
    private bool HandlePoint(in TimelinePoint point, bool arrivedByJump)
    {
        switch (point.Kind)
        {
            case PointKind.Marker:
                switch (point.Marker)
                {
                    case DestinationMarker destination:
                        _instance.OnMarker(destination);
                        return false;
                    case TransitionMarker transition:
                        if (!_instance.MarkerPasses(transition)) return false;
                        if (!_description.TryGetMarkerPosition(transition.Destination, out double dest)) return false;
                        if (transition.Quantization == Quantization.None || !_description.NextGrid(Position, transition.Quantization, true, out double at) || at <= Position + Epsilon)
                        {
                            Jump(dest);
                            return true;
                        }
                        _pendingJump = true;
                        _pendingJumpAt = at;
                        _pendingJumpTo = dest;
                        return false;
                    case SustainPoint sustain:
                        if (!_instance.MarkerPasses(sustain)) return false;
                        Holding = true;
                        _instance.OnSustain();
                        return true;
                }
                return false;
            case PointKind.LoopEnd:
            {
                if (arrivedByJump) return false;
                var loop = (LoopRegion)point.Marker;
                if (!_instance.MarkerPasses(loop)) return false;
                Jump(loop.Position);
                return true;
            }
            case PointKind.RegionStart:
                return Arm((TransitionRegion)point.Marker);
            case PointKind.RegionEnd:
                if (!arrivedByJump && ReferenceEquals(_armed, point.Marker)) _armed = null;
                return false;
        }
        return false;
    }

    /// <returns>true when arming caused an immediate jump.</returns>
    private bool Arm(TransitionRegion region)
    {
        if (region.Quantization == Quantization.None)
        {
            if (_instance.MarkerPasses(region) && _description.TryGetMarkerPosition(region.Destination, out double dest))
            {
                Jump(dest);
                return true;
            }
            return false;
        }
        if (!_description.NextGrid(Position, region.Quantization, true, out double at)) return false;
        _armed = region;
        _armedAt = at;
        return false;
    }

    private void ArmRegionContaining(double position)
    {
        TimelinePoint[] points = _description.Points;
        for (int i = 0; i < points.Length; i++)
        {
            if (points[i].Kind != PointKind.RegionStart) continue;
            var region = (TransitionRegion)points[i].Marker;
            if (position >= region.Position - Epsilon && position < region.Position + region.Length - Epsilon)
            {
                if (Arm(region)) return;
            }
        }
    }

    private void Jump(double destination)
    {
        if (++_jumps > MaxJumpsPerTick) return;
        Position = destination;
        _inclusive = true;
        _pendingJump = false;
        _armed = null;
        _lastBeat = destination;
        _beatInclusive = true;
        ProcessBeats(destination);
        _instance.OnTimelineJump(destination);
        ArmRegionContaining(destination);
    }
}
