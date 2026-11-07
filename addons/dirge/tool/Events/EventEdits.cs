#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using Dirge.Project;
using Dirge.Serialization;
using Dirge.Tool.Core;

namespace Dirge.Tool.Events;

/// <summary>
/// Every project write of the event editor. Each helper records a reversible <see cref="ProjectDocument.Mutate"/>
/// with <see cref="ChangeScope.EventContent"/> and the event id as subject (path edits use <see cref="ChangeScope.Events"/>).
/// </summary>
public sealed class EventEdits
{
    private readonly EventEditorState _state;

    public EventEdits(EventEditorState state) => _state = state;

    private ProjectDocument Doc => _state.Document;
    private DirgeId EventId => _state.EventId;

    /// <summary>Generic property edit: <paramref name="set"/> is applied with the new value and reverted with the old one.</summary>
    public void Set<T>(string label, T oldValue, T newValue, Action<T> set, ChangeScope scope = ChangeScope.EventContent)
    {
        if (EqualityComparer<T>.Default.Equals(oldValue, newValue)) return;
        Doc.Mutate(label, scope, () => set(newValue), () => set(oldValue), EventId);
    }

    /// <summary>Continuous variant for drags; the same <paramref name="token"/> merges into one undo entry.</summary>
    public void SetContinuous<T>(object token, string label, T oldValue, T newValue, Action<T> set)
    {
        Doc.MutateContinuous(token, label, ChangeScope.EventContent, () => set(newValue), () => set(oldValue), EventId);
    }

    public void EndContinuous() => Doc.Undo.EndContinuous();

    // ---- tracks ----------------------------------------------------------------------------------------------------

    public Track AddTrack(string? name = null)
    {
        AudioEvent ev = Require();
        var track = new Track { Name = name ?? $"Track {ev.Tracks.Count + 1}" };
        Doc.Mutate("Add track", ChangeScope.EventContent,
            () => { if (!ev.Tracks.Contains(track)) ev.Tracks.Add(track); },
            () => ev.Tracks.Remove(track), EventId);
        return track;
    }

    public void RemoveTrack(Track track)
    {
        AudioEvent ev = Require();
        int index = ev.Tracks.IndexOf(track);
        if (index < 0) return;
        Doc.Mutate("Remove track", ChangeScope.EventContent,
            () => ev.Tracks.Remove(track),
            () => ev.Tracks.Insert(Math.Min(index, ev.Tracks.Count), track), EventId);
    }

    // ---- instruments -----------------------------------------------------------------------------------------------

    public void AddInstrument(Track track, Instrument instrument)
    {
        Doc.Mutate($"Add {TypeLabel(instrument)}", ChangeScope.EventContent,
            () => { if (!track.Instruments.Contains(instrument)) track.Instruments.Add(instrument); },
            () => track.Instruments.Remove(instrument), EventId);
    }

    public void AddInstruments(List<(Track Track, Instrument Instrument)> items, string label)
    {
        if (items.Count == 0) return;
        Doc.Mutate(label, ChangeScope.EventContent,
            () => { foreach (var (t, i) in items) if (!t.Instruments.Contains(i)) t.Instruments.Add(i); },
            () => { foreach (var (t, i) in items) t.Instruments.Remove(i); }, EventId);
    }

    public void RemoveInstruments(IReadOnlyList<DirgeId> ids)
    {
        var removed = new List<(Track Track, int Index, Instrument Instrument)>();
        for (int i = 0; i < ids.Count; i++)
        {
            Instrument? inst = _state.FindInstrument(ids[i], out Track? track);
            if (inst is not null && track is not null) removed.Add((track, track.Instruments.IndexOf(inst), inst));
        }
        if (removed.Count == 0) return;
        removed.Sort((a, b) => b.Index.CompareTo(a.Index));
        Doc.Mutate(removed.Count == 1 ? "Delete instrument" : $"Delete {removed.Count} instruments", ChangeScope.EventContent,
            () => { foreach (var r in removed) r.Track.Instruments.Remove(r.Instrument); },
            () =>
            {
                for (int i = removed.Count - 1; i >= 0; i--)
                    removed[i].Track.Instruments.Insert(Math.Min(removed[i].Index, removed[i].Track.Instruments.Count), removed[i].Instrument);
            }, EventId);
    }

    /// <summary>Move an instrument to another track (keeps placement).</summary>
    public void MoveInstrumentToTrack(Instrument instrument, Track from, Track to)
    {
        if (ReferenceEquals(from, to)) return;
        int index = from.Instruments.IndexOf(instrument);
        Doc.Mutate("Move instrument", ChangeScope.EventContent,
            () => { from.Instruments.Remove(instrument); if (!to.Instruments.Contains(instrument)) to.Instruments.Add(instrument); },
            () => { to.Instruments.Remove(instrument); from.Instruments.Insert(Math.Min(index, from.Instruments.Count), instrument); }, EventId);
    }

    public void SetPlacements(object token, string label, List<PlacementEdit> edits)
    {
        if (edits.Count == 0) return;
        PlacementEdit[] copy = edits.ToArray();
        Doc.MutateContinuous(token, label, ChangeScope.EventContent,
            () => { for (int i = 0; i < copy.Length; i++) { copy[i].Instrument.Placement.Start = copy[i].NewStart; copy[i].Instrument.Placement.Length = copy[i].NewLength; } },
            () => { for (int i = 0; i < copy.Length; i++) { copy[i].Instrument.Placement.Start = copy[i].OldStart; copy[i].Instrument.Placement.Length = copy[i].OldLength; } },
            EventId);
    }

    public void SetMarkerPositions(object token, string label, List<MarkerEdit> edits)
    {
        if (edits.Count == 0) return;
        MarkerEdit[] copy = edits.ToArray();
        Doc.MutateContinuous(token, label, ChangeScope.EventContent,
            () => { for (int i = 0; i < copy.Length; i++) { copy[i].Marker.Position = copy[i].NewPosition; SetRegionLength(copy[i].Marker, copy[i].NewLength); } },
            () => { for (int i = 0; i < copy.Length; i++) { copy[i].Marker.Position = copy[i].OldPosition; SetRegionLength(copy[i].Marker, copy[i].OldLength); } },
            EventId);
    }

    public static float RegionLength(LogicMarker marker) => marker switch
    {
        LoopRegion l => l.Length,
        TransitionRegion t => t.Length,
        MagnetRegion m => m.Length,
        _ => 0f,
    };

    public static void SetRegionLength(LogicMarker marker, float length)
    {
        switch (marker)
        {
            case LoopRegion l: l.Length = length; break;
            case TransitionRegion t: t.Length = length; break;
            case MagnetRegion m: m.Length = length; break;
        }
    }

    public static bool IsRegion(LogicMarker marker) => marker is LoopRegion or TransitionRegion or MagnetRegion;

    // ---- markers ---------------------------------------------------------------------------------------------------

    public void AddMarker(LogicMarker marker)
    {
        AudioEvent ev = Require();
        Doc.Mutate($"Add {MarkerLabel(marker)}", ChangeScope.EventContent,
            () => { if (!ev.Markers.Contains(marker)) ev.Markers.Add(marker); },
            () => ev.Markers.Remove(marker), EventId);
    }

    public void RemoveMarkers(IReadOnlyList<DirgeId> ids)
    {
        AudioEvent ev = Require();
        var removed = new List<(int Index, LogicMarker Marker)>();
        for (int i = 0; i < ids.Count; i++)
        {
            LogicMarker? m = _state.FindMarker(ids[i]);
            if (m is not null) removed.Add((ev.Markers.IndexOf(m), m));
        }
        if (removed.Count == 0) return;
        removed.Sort((a, b) => b.Index.CompareTo(a.Index));
        Doc.Mutate(removed.Count == 1 ? "Delete marker" : $"Delete {removed.Count} markers", ChangeScope.EventContent,
            () => { foreach (var r in removed) ev.Markers.Remove(r.Marker); },
            () => { for (int i = removed.Count - 1; i >= 0; i--) ev.Markers.Insert(Math.Min(removed[i].Index, ev.Markers.Count), removed[i].Marker); },
            EventId);
    }

    public void AddTempoMarker(TempoMarker marker)
    {
        AudioEvent ev = Require();
        Doc.Mutate("Add tempo marker", ChangeScope.EventContent,
            () => { if (!ev.Tempo.Contains(marker)) { ev.Tempo.Add(marker); SortTempo(ev); } },
            () => ev.Tempo.Remove(marker), EventId);
    }

    public void RemoveTempoMarker(TempoMarker marker)
    {
        AudioEvent ev = Require();
        Doc.Mutate("Remove tempo marker", ChangeScope.EventContent,
            () => ev.Tempo.Remove(marker),
            () => { if (!ev.Tempo.Contains(marker)) { ev.Tempo.Add(marker); SortTempo(ev); } }, EventId);
    }

    public void SetTempoPosition(TempoMarker marker, float position)
    {
        AudioEvent ev = Require();
        float old = marker.Position;
        if (old == position) return;
        Doc.Mutate("Move tempo marker", ChangeScope.EventContent,
            () => { marker.Position = position; SortTempo(ev); },
            () => { marker.Position = old; SortTempo(ev); }, EventId);
    }

    private static void SortTempo(AudioEvent ev) => ev.Tempo.Sort(static (a, b) => a.Position.CompareTo(b.Position));

    // ---- lists (parameters, conditions, entries, lanes, modulators, curve points) -------------------------------------

    public void ListAdd<T>(string label, List<T> list, T item, ChangeScope scope = ChangeScope.EventContent)
    {
        Doc.Mutate(label, scope,
            () => { if (!list.Contains(item)) list.Add(item); },
            () => list.Remove(item), EventId);
    }

    public void ListRemove<T>(string label, List<T> list, T item, ChangeScope scope = ChangeScope.EventContent)
    {
        int index = list.IndexOf(item);
        if (index < 0) return;
        Doc.Mutate(label, scope,
            () => list.Remove(item),
            () => list.Insert(Math.Min(index, list.Count), item), EventId);
    }

    /// <summary>Replace the whole point list of a curve (struct points; sorted by X after apply).</summary>
    public void SetCurvePoints(object? token, string label, Curve curve, List<CurvePoint> newPoints)
    {
        var old = new List<CurvePoint>(curve.Points);
        var fresh = new List<CurvePoint>(newPoints);
        fresh.Sort(static (a, b) => a.X.CompareTo(b.X));
        void Apply() { curve.Points.Clear(); curve.Points.AddRange(fresh); }
        void Revert() { curve.Points.Clear(); curve.Points.AddRange(old); }
        if (token is null) Doc.Mutate(label, ChangeScope.EventContent, Apply, Revert, EventId);
        else Doc.MutateContinuous(token, label, ChangeScope.EventContent, Apply, Revert, EventId);
    }

    // ---- clipboard / clone -------------------------------------------------------------------------------------------

    /// <summary>Deep copy through the project JSON contract; the copy gets a fresh id.</summary>
    public static Instrument Clone(Instrument instrument)
    {
        string json = JsonSerializer.Serialize(instrument, ProjectJson.Options);
        Instrument copy = JsonSerializer.Deserialize<Instrument>(json, ProjectJson.Options)!;
        copy.Id = DirgeId.New();
        for (int i = 0; i < copy.Modulators.Count; i++) copy.Modulators[i].Id = DirgeId.New();
        return copy;
    }

    public static LogicMarker Clone(LogicMarker marker)
    {
        string json = JsonSerializer.Serialize(marker, ProjectJson.Options);
        LogicMarker copy = JsonSerializer.Deserialize<LogicMarker>(json, ProjectJson.Options)!;
        copy.Id = DirgeId.New();
        return copy;
    }

    // ---- factories ---------------------------------------------------------------------------------------------------

    public static Instrument CreateInstrument(InstrumentKind kind, DirgeId sheet, float start, float length) => Fill(kind switch
    {
        InstrumentKind.Clip => new ClipInstrument { Name = "Clip" },
        InstrumentKind.Multi => new MultiInstrument { Name = "Multi" },
        InstrumentKind.Scatterer => new ScattererInstrument { Name = "Scatterer" },
        InstrumentKind.Silence => new SilenceInstrument { Name = "Silence" },
        InstrumentKind.Event => new EventInstrument { Name = "Event" },
        InstrumentKind.Programmer => new ProgrammerInstrument { Name = "Programmer" },
        InstrumentKind.Command => new CommandInstrument { Name = "Command" },
        InstrumentKind.Snapshot => new SnapshotInstrument { Name = "Snapshot" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    }, sheet, start, length);

    private static Instrument Fill(Instrument instrument, DirgeId sheet, float start, float length)
    {
        instrument.Placement.Parameter = sheet;
        instrument.Placement.Start = start;
        instrument.Placement.Length = length;
        return instrument;
    }

    public static LogicMarker CreateMarker(MarkerKind kind, float position, float length) => kind switch
    {
        MarkerKind.Loop => new LoopRegion { Name = "Loop", Position = position, Length = length },
        MarkerKind.Transition => new TransitionMarker { Name = "To", Position = position },
        MarkerKind.TransitionRegion => new TransitionRegion { Name = "To", Position = position, Length = length },
        MarkerKind.Sustain => new SustainPoint { Name = "Sustain", Position = position },
        MarkerKind.Destination => new DestinationMarker { Name = "A", Position = position },
        MarkerKind.Magnet => new MagnetRegion { Name = "Magnet", Position = position, Length = length },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static InstrumentKind KindOf(Instrument instrument) => instrument switch
    {
        ClipInstrument => InstrumentKind.Clip,
        MultiInstrument => InstrumentKind.Multi,
        ScattererInstrument => InstrumentKind.Scatterer,
        SilenceInstrument => InstrumentKind.Silence,
        EventInstrument => InstrumentKind.Event,
        ProgrammerInstrument => InstrumentKind.Programmer,
        CommandInstrument => InstrumentKind.Command,
        SnapshotInstrument => InstrumentKind.Snapshot,
        _ => InstrumentKind.Silence,
    };

    public static string TypeLabel(Instrument instrument) => KindOf(instrument).ToString();

    public static string MarkerLabel(LogicMarker marker) => marker switch
    {
        LoopRegion => "Loop region",
        TransitionMarker => "Transition marker",
        TransitionRegion => "Transition region",
        SustainPoint => "Sustain point",
        DestinationMarker => "Destination marker",
        MagnetRegion => "Magnet region",
        _ => "Marker",
    };

    private AudioEvent Require() => _state.Event ?? throw new InvalidOperationException("No event is being edited.");
}

public enum InstrumentKind { Clip, Multi, Scatterer, Silence, Event, Programmer, Command, Snapshot }
public enum MarkerKind { Loop, Transition, TransitionRegion, Sustain, Destination, Magnet }

public readonly record struct PlacementEdit(Instrument Instrument, float OldStart, float OldLength, float NewStart, float NewLength);
public readonly record struct MarkerEdit(LogicMarker Marker, float OldPosition, float OldLength, float NewPosition, float NewLength);
