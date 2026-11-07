using System;
using Dirge.Project;

namespace Dirge.Tests;

/// <summary>
/// Builds valid <see cref="AudioProject"/>s for tests: always starts with a "Master" bus. Every Add* returns the created
/// object so a test can mutate it afterwards (typically to make it invalid). Ids are deterministic per builder so
/// serialized output is stable across runs.
/// </summary>
public sealed class ProjectBuilder
{
    private int _nextId = 1;

    public AudioProject Project { get; }
    public Bus Master { get; }

    public ProjectBuilder(string name = "Test")
    {
        Project = new AudioProject { Name = name };
        Master = new Bus { Id = NextId(), Name = "Master", Parent = DirgeId.None };
        Project.Mixer.Buses.Add(Master);
    }

    /// <summary>Sequential, reproducible ids (…0001, …0002, …).</summary>
    public DirgeId NextId() => new(new Guid(_nextId++, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

    public Bus AddBus(string name, Bus? parent = null, float volumeDb = 0f)
    {
        var bus = new Bus { Id = NextId(), Name = name, Parent = (parent ?? Master).Id, VolumeDb = volumeDb };
        Project.Mixer.Buses.Add(bus);
        return bus;
    }

    public BusEffect AddEffect(Bus bus, string typeName, params (string Name, float Value)[] parameters)
    {
        var effect = new BusEffect { Id = NextId(), TypeName = typeName };
        foreach ((string key, float value) in parameters) effect.Parameters[key] = value;
        bus.Effects.Add(effect);
        return effect;
    }

    public Vca AddVca(string name, float volumeDb = 0f, params Bus[] buses)
    {
        var vca = new Vca { Id = NextId(), Name = name, VolumeDb = volumeDb };
        Project.Mixer.Vcas.Add(vca);
        foreach (Bus bus in buses) bus.Vcas.Add(vca.Id);
        return vca;
    }

    public ParameterDefinition AddGlobalParameter(string name, float minimum = 0f, float maximum = 1f, float @default = 0f, ParameterKind kind = ParameterKind.Continuous)
    {
        ParameterDefinition p = NewParameter(name, minimum, maximum, @default, kind);
        Project.GlobalParameters.Add(p);
        return p;
    }

    public ParameterDefinition AddLocalParameter(AudioEvent ev, string name, float minimum = 0f, float maximum = 1f, float @default = 0f, ParameterKind kind = ParameterKind.Continuous)
    {
        ParameterDefinition p = NewParameter(name, minimum, maximum, @default, kind);
        ev.LocalParameters.Add(p);
        return p;
    }

    private ParameterDefinition NewParameter(string name, float minimum, float maximum, float @default, ParameterKind kind)
        => new() { Id = NextId(), Name = name, Minimum = minimum, Maximum = maximum, Default = @default, Kind = kind };

    public Snapshot AddSnapshot(string name, int priority = 0, SnapshotMode mode = SnapshotMode.Blending)
    {
        var snapshot = new Snapshot { Id = NextId(), Name = name, Priority = priority, Mode = mode };
        Project.Snapshots.Add(snapshot);
        return snapshot;
    }

    public AudioEvent AddEvent(string path, Bus? bus = null)
    {
        var ev = new AudioEvent { Id = NextId(), Path = path, Bus = (bus ?? Master).Id };
        Project.Events.Add(ev);
        return ev;
    }

    public Track AddTrack(AudioEvent ev, string name = "Track", float volumeDb = 0f)
    {
        var track = new Track { Id = NextId(), Name = name, VolumeDb = volumeDb };
        ev.Tracks.Add(track);
        return track;
    }

    /// <summary>Adds <paramref name="instrument"/> to the track on the timeline at <paramref name="start"/>.</summary>
    public T AddInstrument<T>(Track track, T instrument, float start = 0f, float length = 0f) where T : Instrument
    {
        instrument.Id = NextId();
        if (string.IsNullOrEmpty(instrument.Name)) instrument.Name = typeof(T).Name;
        instrument.Placement = new Placement { Parameter = DirgeId.None, Start = start, Length = length };
        track.Instruments.Add(instrument);
        return instrument;
    }

    /// <summary>Adds <paramref name="instrument"/> on the parameter sheet of <paramref name="parameter"/> covering [start, start+length).</summary>
    public T AddSheetInstrument<T>(Track track, T instrument, ParameterDefinition parameter, float start, float length) where T : Instrument
    {
        instrument.Id = NextId();
        if (string.IsNullOrEmpty(instrument.Name)) instrument.Name = typeof(T).Name;
        instrument.Placement = new Placement { Parameter = parameter.Id, Start = start, Length = length };
        track.Instruments.Add(instrument);
        return instrument;
    }

    public ClipInstrument AddClip(Track track, string asset, float start = 0f, float length = 0f)
        => AddInstrument(track, new ClipInstrument { Asset = new AssetRef(asset) }, start, length);

    public T AddMarker<T>(AudioEvent ev, T marker, float position, string? name = null) where T : LogicMarker
    {
        marker.Id = NextId();
        marker.Name = name ?? typeof(T).Name;
        marker.Position = position;
        ev.Markers.Add(marker);
        return marker;
    }

    public TempoMarker AddTempo(AudioEvent ev, float bpm = 120f, float position = 0f, int beatsPerBar = 4)
    {
        var tempo = new TempoMarker { Position = position, Bpm = bpm, BeatsPerBar = beatsPerBar };
        ev.Tempo.Add(tempo);
        return tempo;
    }

    public AutomationLane AddAutomation(AudioEvent ev, PropertyRef target, ParameterDefinition parameter, params CurvePoint[] points)
    {
        var lane = new AutomationLane { Id = NextId(), Target = target, Parameter = parameter.Id };
        lane.Curve.Points.AddRange(points);
        ev.Automation.Add(lane);
        return lane;
    }

    public T AddModulator<T>(AudioEvent ev, T modulator, PropertyRef target, float amount) where T : Modulator
    {
        modulator.Id = NextId();
        modulator.Target = target;
        modulator.Amount = amount;
        ev.Modulators.Add(modulator);
        return modulator;
    }

    public T AddModulator<T>(Instrument instrument, T modulator, PropertyRef target, float amount) where T : Modulator
    {
        modulator.Id = NextId();
        modulator.Target = target;
        modulator.Amount = amount;
        instrument.Modulators.Add(modulator);
        return modulator;
    }

    public AudioBank AddBank(string name, params AudioEvent[] events)
    {
        var bank = new AudioBank { Id = NextId(), Name = name };
        foreach (AudioEvent ev in events) bank.Events.Add(ev.Id);
        Project.Banks.Add(bank);
        return bank;
    }

    public AudioProject Build() => Project;

    /// <summary>Master + one event with one clip; the smallest project worth playing.</summary>
    public static ProjectBuilder Minimal(string eventPath = "Test/Beep", string asset = "res://beep.wav", double assetLength = 1.0)
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent(eventPath);
        b.AddClip(b.AddTrack(ev), asset);
        b.Project.AssetLengths[asset] = assetLength;
        return b;
    }
}
