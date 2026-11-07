using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Runtime;

/// <summary>
/// Runtime view of one <see cref="AudioEvent"/>: precomputed instrument/marker/tempo data shared by all its instances,
/// the instance pool and the started-instance list used for MaxInstances/stealing.
/// </summary>
public sealed class EventDescription
{
    private const double Epsilon = 1e-9;

    private readonly DirgeSystem _system;
    private readonly List<EventInstance> _started = new();
    private readonly Stack<EventInstance> _pool = new();
    private readonly TempoMarker[] _tempo;
    private readonly DirgeId[] _markerIds;
    private readonly double[] _markerPositions;

    /// <summary>Track instruments first, then every transition timeline's instruments (<see cref="InstrumentInfo.Transition"/> set).</summary>
    internal readonly InstrumentInfo[] Instruments;
    internal readonly TimelinePoint[] Points;
    /// <summary>Seek snap regions, in authored order.</summary>
    internal readonly MagnetRegion[] Magnets;
    internal readonly AutomationLane[] EventVolumeLanes;
    internal readonly AutomationLane[] EventPitchLanes;
    internal readonly ModInfo[] EventModulators;
    internal readonly float EventMaxRelease;
    /// <summary>Indices into <see cref="Parameters"/> whose definition has a built-in source.</summary>
    internal readonly int[] BuiltInParameters;
    internal readonly string BusName;
    internal readonly int BusIndex;
    internal double LastStartTime = double.NegativeInfinity;

    public AudioEvent Event { get; }
    public DirgeId Id => Event.Id;
    public string Path => Event.Path;
    public bool Is3D => Event.Macros.Is3D;
    /// <summary>Not Persistent, no loop region and no looping instrument on the timeline.</summary>
    public bool IsOneshot { get; }
    /// <summary>Timeline length: max(instrument end, marker end); 0 when the event only has parameter sheets.</summary>
    public double Length { get; }
    public IReadOnlyList<ParameterDefinition> Parameters => Event.LocalParameters;
    /// <summary>Instances created and not yet destroyed (including nested children).</summary>
    public int InstanceCount { get; private set; }

    internal DirgeSystem System => _system;
    internal IReadOnlyList<EventInstance> Started => _started;

    internal EventDescription(DirgeSystem system, AudioEvent ev, AudioProject project, IAssetCatalog assets)
    {
        _system = system;
        Event = ev;

        BusName = "Master";
        BusIndex = 0;
        for (int i = 0; i < project.Mixer.Buses.Count; i++)
        {
            Bus bus = project.Mixer.Buses[i];
            if (bus.Id == ev.Bus) { BusName = bus.Name; BusIndex = i; break; }
            if (ev.Bus.IsNone && bus.Parent.IsNone) { BusName = bus.Name; BusIndex = i; }
        }

        var instruments = new List<InstrumentInfo>();
        bool loopingInstrument = false;
        for (int t = 0; t < ev.Tracks.Count; t++)
        {
            Track track = ev.Tracks[t];
            for (int i = 0; i < track.Instruments.Count; i++)
            {
                var info = new InstrumentInfo(ev, track, track.Instruments[i], project, assets, null);
                instruments.Add(info);
                if (info.OnTimeline && track.Instruments[i].Loop) loopingInstrument = true;
            }
        }
        for (int i = 0; i < ev.Markers.Count; i++)
        {
            List<Instrument>? transitionInstruments = ev.Markers[i] switch
            {
                TransitionMarker tm when tm.TransitionLength > 0f => tm.TransitionInstruments,
                TransitionRegion tr when tr.TransitionLength > 0f => tr.TransitionInstruments,
                _ => null,
            };
            if (transitionInstruments is null) continue;
            for (int k = 0; k < transitionInstruments.Count; k++)
                instruments.Add(new InstrumentInfo(ev, InstrumentInfo.NoTrack, transitionInstruments[k], project, assets, ev.Markers[i]));
        }
        Instruments = instruments.ToArray();

        var points = new List<TimelinePoint>();
        var markerIds = new List<DirgeId>();
        var markerPositions = new List<double>();
        var magnets = new List<MagnetRegion>();
        bool hasLoop = false;
        for (int i = 0; i < ev.Markers.Count; i++)
        {
            LogicMarker m = ev.Markers[i];
            markerIds.Add(m.Id);
            markerPositions.Add(m.Position);
            switch (m)
            {
                case LoopRegion loop:
                    hasLoop = true;
                    points.Add(new TimelinePoint(loop.Position + Math.Max(0f, loop.Length), loop, PointKind.LoopEnd));
                    break;
                case TransitionRegion region:
                    points.Add(new TimelinePoint(region.Position, region, PointKind.RegionStart));
                    points.Add(new TimelinePoint(region.Position + Math.Max(0f, region.Length), region, PointKind.RegionEnd));
                    break;
                case MagnetRegion magnet:
                    magnets.Add(magnet);
                    break;
                default:
                    points.Add(new TimelinePoint(m.Position, m, PointKind.Marker));
                    break;
            }
        }
        points.Sort(ComparePoints);
        Points = points.ToArray();
        Magnets = magnets.ToArray();
        _markerIds = markerIds.ToArray();
        _markerPositions = markerPositions.ToArray();

        var tempo = new List<TempoMarker>();
        for (int i = 0; i < ev.Tempo.Count; i++) if (ev.Tempo[i].Bpm > 0f) tempo.Add(ev.Tempo[i]);
        tempo.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        _tempo = tempo.ToArray();

        EventVolumeLanes = ModEval.CollectLanes(ev.Automation, PropertyKind.EventVolumeDb, ev.Id);
        EventPitchLanes = ModEval.CollectLanes(ev.Automation, PropertyKind.EventPitch, ev.Id);
        EventModulators = ModEval.Collect(ev.Modulators, PropertyKind.EventVolumeDb, PropertyKind.EventPitch, ev.Id);
        EventMaxRelease = ModEval.MaxVolumeRelease(EventModulators);

        var builtIns = new List<int>();
        for (int i = 0; i < ev.LocalParameters.Count; i++)
            if (ev.LocalParameters[i].BuiltIn != BuiltInParameter.None) builtIns.Add(i);
        BuiltInParameters = builtIns.ToArray();

        double length = 0;
        for (int i = 0; i < Instruments.Length; i++)
            if (Instruments[i].OnTimeline && Instruments[i].Transition is null) length = Math.Max(length, Instruments[i].LengthContribution);
        for (int i = 0; i < Points.Length; i++) length = Math.Max(length, Points[i].Position);
        for (int i = 0; i < Magnets.Length; i++) length = Math.Max(length, Magnets[i].Position + Math.Max(0f, Magnets[i].Length));
        Length = length;
        IsOneshot = !ev.Macros.Persistent && !hasLoop && !loopingInstrument;
    }

    private static int ComparePoints(TimelinePoint a, TimelinePoint b)
    {
        int c = a.Position.CompareTo(b.Position);
        return c != 0 ? c : ((int)a.Kind).CompareTo((int)b.Kind);
    }

    /// <summary>Resolve references to other events once every description exists.</summary>
    internal void Link()
    {
        for (int i = 0; i < Instruments.Length; i++)
        {
            EventInstrument? nested = Instruments[i].EventInstrument;
            if (nested is not null && _system.TryGetEvent(nested.Event, out EventDescription? target)) Instruments[i].NestedEvent = target;
        }
    }

    /// <summary>Pooled. MaxInstances/stealing are applied by <see cref="EventInstance.Start"/>, not here.</summary>
    public EventInstance CreateInstance()
    {
        EventInstance instance = CreateInstance(null);
        _system.Recorder?.Created(instance, "CreateInstance", Path);
        return instance;
    }

    /// <summary>Nested instances (event instruments) are owned and updated by <paramref name="parent"/>, never by the system list.</summary>
    internal EventInstance CreateInstance(EventInstance? parent)
    {
        EventInstance instance = _pool.Count > 0 ? _pool.Pop() : new EventInstance(_system, this);
        instance.Reinitialize();
        if (parent is not null) instance.AttachToParent(parent);
        InstanceCount++;
        _system.OnInstanceCreated(instance);
        return instance;
    }

    internal void OnStarted(EventInstance instance)
    {
        if (!_started.Contains(instance)) _started.Add(instance);
    }

    internal void OnStopped(EventInstance instance) => _started.Remove(instance);

    internal void Recycle(EventInstance instance)
    {
        InstanceCount--;
        _started.Remove(instance);
        _pool.Push(instance);
        _system.OnInstanceDestroyed();
    }

    /// <summary>
    /// <see cref="StealingMode.Virtualize"/>: the first MaxInstances started instances are real, later ones virtual.
    /// Re-evaluated each tick so a virtual instance becomes real once an older one stops.
    /// </summary>
    internal void ApplyVirtualizeStealing()
    {
        EventMacros macros = Event.Macros;
        if (macros.Stealing != StealingMode.Virtualize || macros.MaxInstances <= 0) return;
        for (int i = 0; i < _started.Count; i++) _started[i].StealVirtual = i >= macros.MaxInstances;
    }

    /// <summary>Stop every started instance (command instruments).</summary>
    internal void StopAll(StopMode mode)
    {
        for (int i = _started.Count - 1; i >= 0; i--)
            if (i < _started.Count) _started[i].Stop(mode);
    }

    internal bool TryGetMarkerPosition(DirgeId marker, out double position)
    {
        for (int i = 0; i < _markerIds.Length; i++)
        {
            if (_markerIds[i] != marker) continue;
            position = _markerPositions[i];
            return true;
        }
        position = 0;
        return false;
    }

    /// <summary>Apply <see cref="MagnetRegion"/>s to a seek target: the first region containing it wins.</summary>
    internal double Magnetize(double position)
    {
        for (int i = 0; i < Magnets.Length; i++)
        {
            MagnetRegion m = Magnets[i];
            if (position >= m.Position - Epsilon && position < m.Position + m.Length - Epsilon) return m.Position;
        }
        return position;
    }

    // ---- tempo ---------------------------------------------------------------------------------------------------

    internal float BpmAt(double position)
    {
        int s = TempoSegment(position);
        return s < 0 ? 0f : _tempo[s].Bpm;
    }

    /// <summary>Index of the tempo marker governing <paramref name="position"/>; −1 before the first marker or without tempo.</summary>
    private int TempoSegment(double position)
    {
        int s = -1;
        for (int i = 0; i < _tempo.Length; i++)
        {
            if (_tempo[i].Position <= position + Epsilon) s = i;
            else break;
        }
        return s;
    }

    /// <summary>Next beat at (inclusive) or strictly after <paramref name="after"/>. Bars restart at every tempo marker.</summary>
    internal bool NextBeat(double after, bool inclusive, out double at, out int bar, out int beat, out float bpm)
        => NextGridPoint(after, inclusive, false, out at, out bar, out beat, out bpm);

    /// <summary>Next quantization point for a transition. Returns false when the grid cannot be resolved (no tempo, no markers).</summary>
    internal bool NextGrid(double position, Quantization quantization, bool inclusive, out double at)
    {
        switch (quantization)
        {
            case Quantization.Beat:
                return NextGridPoint(position, inclusive, false, out at, out _, out _, out _);
            case Quantization.Bar:
                return NextGridPoint(position, inclusive, true, out at, out _, out _, out _);
            case Quantization.Marker:
            {
                at = double.PositiveInfinity;
                bool found = false;
                for (int i = 0; i < Points.Length; i++)
                {
                    if (Points[i].Kind != PointKind.Marker || Points[i].Marker is not DestinationMarker) continue;
                    double p = Points[i].Position;
                    bool ok = inclusive ? p >= position - Epsilon : p > position + Epsilon;
                    if (ok && p < at) { at = p; found = true; }
                }
                return found;
            }
            default:
                at = position;
                return true;
        }
    }

    private bool NextGridPoint(double after, bool inclusive, bool bars, out double at, out int bar, out int beat, out float bpm)
    {
        at = 0; bar = 1; beat = 1; bpm = 0f;
        if (_tempo.Length == 0) return false;
        int s = TempoSegment(after);
        if (s < 0)
        {
            at = _tempo[0].Position;
            bpm = _tempo[0].Bpm;
            return true;
        }
        TempoMarker marker = _tempo[s];
        int beatsPerBar = Math.Max(1, marker.BeatsPerBar);
        double beatLength = 60.0 / marker.Bpm;
        double unit = bars ? beatLength * beatsPerBar : beatLength;
        double k = (after - marker.Position) / unit;
        double kc = Math.Ceiling(k - 1e-7);
        double candidate = marker.Position + kc * unit;
        if (!inclusive && candidate <= after + Epsilon)
        {
            kc += 1;
            candidate = marker.Position + kc * unit;
        }
        if (s + 1 < _tempo.Length && candidate >= _tempo[s + 1].Position - Epsilon)
        {
            at = _tempo[s + 1].Position;
            bpm = _tempo[s + 1].Bpm;
            return true;
        }
        at = candidate;
        bpm = marker.Bpm;
        int index = (int)Math.Max(0, kc);
        if (bars) { bar = index + 1; beat = 1; }
        else { bar = index / beatsPerBar + 1; beat = index % beatsPerBar + 1; }
        return true;
    }
}
