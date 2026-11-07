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
/// <para>
/// A transition with <c>TransitionLength</c> &gt; 0 runs a transition timeline: <see cref="Position"/> freezes at the point
/// the transition fired, the instance is told to cut the source instruments and run the transition instruments on a local
/// clock (<see cref="TransitionElapsed"/>), and once the clock reaches the length the destination is entered as a normal jump.
/// Beats are not reported while the transition clock runs; a seek cancels it.
/// </para>
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
    /// <summary>Marker whose transition timeline is running, or null.</summary>
    public LogicMarker? Transition;
    /// <summary>Seconds elapsed on the running transition timeline.</summary>
    public double TransitionElapsed;

    private double _transitionLength;
    private double _transitionDestination;
    private bool _inclusive;
    private bool _pendingJump;
    private double _pendingJumpAt;
    private double _pendingJumpTo;
    private TransitionMarker? _pendingJumpMarker;
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

    /// <summary>
    /// Place the playhead without evaluating anything (stopped instances, seeks applied at the next update). Magnet regions
    /// snap the target; a running transition timeline is cancelled (its instruments are cut by the instance).
    /// </summary>
    public void Reset(double position)
    {
        if (Transition is not null)
        {
            LogicMarker marker = Transition;
            Transition = null;
            _instance.OnTransitionEnd(marker);
        }
        Position = _description.Magnetize(position);
        Holding = false;
        _inclusive = true;
        _pendingJump = false;
        _armed = null;
        _lastBeat = Position;
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
            if (Transition is not null)
            {
                double left = _transitionLength - TransitionElapsed;
                double step = Math.Min(remaining, left);
                double from = TransitionElapsed;
                TransitionElapsed += step;
                remaining -= step;
                _instance.OnTransitionSegment(Transition, from, TransitionElapsed);
                if (TransitionElapsed < _transitionLength - Epsilon) return;
                LogicMarker marker = Transition;
                Transition = null;
                _instance.OnTransitionEnd(marker);
                Jump(_transitionDestination);
                if (Holding) return;
                continue;
            }
            double target = Position + remaining;
            int pi = NextPointIndex(points, Position, _inclusive);
            double next = pi >= 0 ? points[pi].Position : double.PositiveInfinity;
            int kind = 0; // 0 = logic point, 1 = pending quantized jump, 2 = armed transition region check
            if (_pendingJump && _pendingJumpAt <= next) { next = _pendingJumpAt; kind = 1; }
            if (_armed is not null && _armedAt <= next) { next = _armedAt; kind = 2; }
            // Snap onto a point within epsilon of the tick end: accumulated dt rounding must not leave the playhead a
            // hair short of a loop end, which the next (exclusive) search would then skip.
            if (next > target + Epsilon)
            {
                MoveTo(target, false);
                return;
            }
            bool arrivedByJump = _inclusive && next <= Position + Epsilon;
            bool pointsHere = pi >= 0 && points[pi].Position <= next + Epsilon;
            bool loopingHere = !arrivedByJump && pointsHere && LoopFiresAt(points, pi, next);
            remaining -= Math.Max(0, next - Position);
            MoveTo(next, loopingHere);
            bool transitioned = false;
            switch (kind)
            {
                case 1:
                    _pendingJump = false;
                    BeginTransition(_pendingJumpMarker!, _pendingJumpTo, _pendingJumpMarker!.TransitionLength);
                    transitioned = true;
                    break;
                case 2:
                {
                    TransitionRegion region = _armed!;
                    if (_instance.MarkerPasses(region) && _description.TryGetMarkerPosition(region.Destination, out double dest))
                    {
                        BeginTransition(region, dest, region.TransitionLength);
                        transitioned = true;
                    }
                    else if (!_description.NextGrid(Position, region.Quantization, false, out _armedAt))
                    {
                        _armed = null;
                    }
                    break;
                }
            }
            // Logic points sharing the position of a quantization check still run when the check did not jump away.
            if (!transitioned && pointsHere)
            {
                for (int j = pi; j < points.Length && points[j].Position <= next + Epsilon; j++)
                    if (HandlePoint(points[j], arrivedByJump)) break;
            }
            if (Holding) return;
        }
    }

    /// <summary>
    /// True when a <see cref="LoopRegion"/> end at the points' position will certainly fire (conditions pass, no probability
    /// roll): the segment ending there is then reported with (start, end] region semantics so instruments ending exactly at
    /// the loop end stay active and get re-seeked by the jump instead of being cut and restarted. No RNG is consumed.
    /// </summary>
    private bool LoopFiresAt(TimelinePoint[] points, int first, double position)
    {
        for (int j = first; j < points.Length && points[j].Position <= position + Epsilon; j++)
        {
            if (points[j].Kind != PointKind.LoopEnd) continue;
            LogicMarker loop = points[j].Marker;
            if (loop.Probability >= 1f && _instance.ConditionsPass(loop.Conditions)) return true;
        }
        return false;
    }

    /// <summary>A transition fired: jump now, or start its transition timeline when it has a length.</summary>
    private void BeginTransition(LogicMarker marker, double destination, float length)
    {
        if (length <= 0f)
        {
            Jump(destination);
            return;
        }
        if (++_jumps > MaxJumpsPerTick) return;
        Transition = marker;
        TransitionElapsed = 0;
        _transitionLength = length;
        _transitionDestination = destination;
        _pendingJump = false;
        _armed = null;
        _instance.OnTransitionBegin(marker);
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

    private void MoveTo(double position, bool loopingHere)
    {
        double from = Position;
        bool inclusive = _inclusive;
        Position = position;
        _inclusive = false;
        ProcessBeats(position);
        _instance.OnTimelineSegment(from, position, inclusive, loopingHere);
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
                            BeginTransition(transition, dest, transition.TransitionLength);
                            return true;
                        }
                        _pendingJump = true;
                        _pendingJumpAt = at;
                        _pendingJumpTo = dest;
                        _pendingJumpMarker = transition;
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
                BeginTransition(region, dest, region.TransitionLength);
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
