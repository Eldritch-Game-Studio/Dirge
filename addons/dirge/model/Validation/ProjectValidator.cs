using System;
using System.Collections.Generic;
using Dirge.Project;

namespace Dirge.Validation;

public enum Severity { Error, Warning }

/// <summary><see cref="Subject"/> is the offending object's id, or <see cref="DirgeId.None"/> for project-level findings.</summary>
public sealed record Diagnostic(Severity Severity, string Code, string Message, DirgeId Subject);

/// <summary>Stable diagnostic codes. Tools and tests key on these, never on message text.</summary>
public static class DiagnosticCodes
{
    public const string MasterBusMissing = "master-bus-missing";
    public const string MasterBusMultiple = "master-bus-multiple";
    public const string MasterBusName = "master-bus-name";
    public const string BusParentMissing = "bus-parent-missing";
    public const string BusCycle = "bus-cycle";
    public const string DuplicateBusName = "duplicate-bus-name";
    public const string DuplicateVcaName = "duplicate-vca-name";
    public const string DuplicateEventPath = "duplicate-event-path";
    public const string DuplicateParameterName = "duplicate-parameter-name";
    public const string EventBusMissing = "event-bus-missing";
    public const string ParameterRefMissing = "parameter-ref-missing";
    public const string EventInstrumentTargetMissing = "event-instrument-target-missing";
    public const string EventInstrumentCycle = "event-instrument-cycle";
    public const string SnapshotTargetMissing = "snapshot-target-missing";
    public const string CommandTargetMissing = "command-target-missing";
    public const string LabeledParameterNoLabels = "labeled-parameter-no-labels";
    public const string CurveUnsorted = "curve-unsorted";
    public const string ParameterRangeInvalid = "parameter-range-invalid";
    public const string EmptyAssetRef = "empty-asset-ref";
    public const string MultiInstrumentEmpty = "multi-instrument-empty";
    public const string RegionLengthInvalid = "region-length-invalid";
    public const string TransitionDestinationInvalid = "transition-destination-invalid";
    public const string TempoBpmInvalid = "tempo-bpm-invalid";
    public const string VcaRefMissing = "vca-ref-missing";
    public const string EffectTypeNameEmpty = "effect-type-name-empty";
}

/// <summary>Structural checks over an <see cref="AudioProject"/>. Pure; safe to run on every edit in the tool and in CI.</summary>
public static class ProjectValidator
{
    public static IReadOnlyList<Diagnostic> Validate(AudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var context = new Context(project);
        ValidateMixer(context);
        ValidateGlobalParameters(context);
        ValidateSnapshots(context);
        ValidateEvents(context);
        ValidateEventInstrumentCycles(context);
        return context.Diagnostics;
    }

    private sealed class Context
    {
        public readonly AudioProject Project;
        public readonly List<Diagnostic> Diagnostics = new();
        public readonly Dictionary<DirgeId, Bus> Buses = new();
        public readonly Dictionary<DirgeId, Vca> Vcas = new();
        public readonly Dictionary<DirgeId, Snapshot> Snapshots = new();
        public readonly Dictionary<DirgeId, AudioEvent> Events = new();
        public readonly HashSet<DirgeId> GlobalParameters = new();

        public Context(AudioProject project)
        {
            Project = project;
            foreach (Bus bus in project.Mixer.Buses) Buses.TryAdd(bus.Id, bus);
            foreach (Vca vca in project.Mixer.Vcas) Vcas.TryAdd(vca.Id, vca);
            foreach (Snapshot snapshot in project.Snapshots) Snapshots.TryAdd(snapshot.Id, snapshot);
            foreach (AudioEvent ev in project.Events) Events.TryAdd(ev.Id, ev);
            foreach (ParameterDefinition p in project.GlobalParameters) GlobalParameters.Add(p.Id);
        }

        public void Error(string code, DirgeId subject, string message) => Diagnostics.Add(new Diagnostic(Severity.Error, code, message, subject));
        public void Warning(string code, DirgeId subject, string message) => Diagnostics.Add(new Diagnostic(Severity.Warning, code, message, subject));

        /// <summary>Bus or VCA property owner exists (effect index checked for effect parameters).</summary>
        public bool MixerTargetExists(PropertyRef target)
        {
            switch (target.Kind)
            {
                case PropertyKind.BusVolumeDb:
                case PropertyKind.BusPitch:
                    return Buses.ContainsKey(target.Owner);
                case PropertyKind.EffectParameter:
                    return Buses.TryGetValue(target.Owner, out Bus? bus)
                           && target.EffectIndex >= 0 && target.EffectIndex < bus.Effects.Count;
                case PropertyKind.VcaVolumeDb:
                    return Vcas.ContainsKey(target.Owner);
                default:
                    return false;
            }
        }
    }

    // ---- mixer -------------------------------------------------------------------------------------------------

    private static void ValidateMixer(Context c)
    {
        MixerGraph mixer = c.Project.Mixer;

        var masters = new List<Bus>();
        foreach (Bus bus in mixer.Buses) if (bus.Parent.IsNone) masters.Add(bus);
        if (masters.Count == 0)
            c.Error(DiagnosticCodes.MasterBusMissing, DirgeId.None, "The mixer has no master bus (a bus with no parent named \"Master\").");
        else if (masters.Count > 1)
            foreach (Bus bus in masters)
                c.Error(DiagnosticCodes.MasterBusMultiple, bus.Id, $"Bus \"{bus.Name}\" has no parent but only one master bus is allowed.");
        foreach (Bus bus in masters)
            if (bus.Name != "Master")
                c.Error(DiagnosticCodes.MasterBusName, bus.Id, $"Master bus must be named \"Master\", found \"{bus.Name}\".");

        var cycleReported = new HashSet<DirgeId>();
        var chain = new HashSet<DirgeId>();
        foreach (Bus bus in mixer.Buses)
        {
            if (!bus.Parent.IsNone && !c.Buses.ContainsKey(bus.Parent))
                c.Error(DiagnosticCodes.BusParentMissing, bus.Id, $"Bus \"{bus.Name}\" routes to a parent bus that does not exist.");

            if (!cycleReported.Contains(bus.Id))
            {
                chain.Clear();
                Bus current = bus;
                while (chain.Add(current.Id))
                {
                    if (current.Parent.IsNone || !c.Buses.TryGetValue(current.Parent, out Bus? parent)) break;
                    if (chain.Contains(parent.Id))
                    {
                        foreach (DirgeId id in chain) cycleReported.Add(id);
                        c.Error(DiagnosticCodes.BusCycle, bus.Id, $"Bus \"{bus.Name}\" is part of a routing cycle.");
                        break;
                    }
                    current = parent;
                }
            }

            foreach (DirgeId vca in bus.Vcas)
                if (!c.Vcas.ContainsKey(vca))
                    c.Error(DiagnosticCodes.VcaRefMissing, bus.Id, $"Bus \"{bus.Name}\" references a VCA that does not exist.");

            foreach (BusEffect effect in bus.Effects)
                if (string.IsNullOrWhiteSpace(effect.TypeName))
                    c.Error(DiagnosticCodes.EffectTypeNameEmpty, effect.Id, $"An effect on bus \"{bus.Name}\" has no TypeName.");
        }

        ReportDuplicates(c, mixer.Buses, static b => b.Name, static b => b.Id, DiagnosticCodes.DuplicateBusName, "bus name");
        ReportDuplicates(c, mixer.Vcas, static v => v.Name, static v => v.Id, DiagnosticCodes.DuplicateVcaName, "VCA name");
    }

    private static void ReportDuplicates<T>(Context c, List<T> items, Func<T, string> key, Func<T, DirgeId> id, string code, string what)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (T item in items)
        {
            string k = key(item);
            seen[k] = seen.TryGetValue(k, out int n) ? n + 1 : 1;
        }
        foreach (T item in items)
            if (seen[key(item)] > 1)
                c.Error(code, id(item), $"Duplicate {what} \"{key(item)}\".");
    }

    // ---- parameters --------------------------------------------------------------------------------------------

    private static void ValidateGlobalParameters(Context c)
    {
        ReportDuplicates(c, c.Project.GlobalParameters, static p => p.Name, static p => p.Id, DiagnosticCodes.DuplicateParameterName, "global parameter name");
        foreach (ParameterDefinition p in c.Project.GlobalParameters) ValidateParameterDefinition(c, p);
    }

    private static void ValidateParameterDefinition(Context c, ParameterDefinition p)
    {
        if (p.Kind == ParameterKind.Labeled && p.Labels.Count == 0)
            c.Error(DiagnosticCodes.LabeledParameterNoLabels, p.Id, $"Labeled parameter \"{p.Name}\" has no labels.");
        if (!(p.Minimum <= p.Default && p.Default <= p.Maximum))
            c.Error(DiagnosticCodes.ParameterRangeInvalid, p.Id, $"Parameter \"{p.Name}\" must satisfy Minimum ≤ Default ≤ Maximum ({p.Minimum} ≤ {p.Default} ≤ {p.Maximum}).");
    }

    private static bool ParameterResolves(Context c, AudioEvent? scope, DirgeId parameter)
    {
        if (scope is not null)
            foreach (ParameterDefinition p in scope.LocalParameters)
                if (p.Id == parameter) return true;
        return c.GlobalParameters.Contains(parameter);
    }

    private static void RequireParameter(Context c, AudioEvent scope, DirgeId parameter, DirgeId subject, string where)
    {
        if (!ParameterResolves(c, scope, parameter))
            c.Error(DiagnosticCodes.ParameterRefMissing, subject, $"{where} references a parameter that is neither local to \"{scope.Path}\" nor global.");
    }

    // ---- snapshots ---------------------------------------------------------------------------------------------

    private static void ValidateSnapshots(Context c)
    {
        foreach (Snapshot snapshot in c.Project.Snapshots)
            foreach (SnapshotValue value in snapshot.Values)
                if (!c.MixerTargetExists(value.Target))
                    c.Error(DiagnosticCodes.SnapshotTargetMissing, snapshot.Id, $"Snapshot \"{snapshot.Name}\" targets a {value.Target.Kind} property whose owner does not exist.");
    }

    // ---- events ------------------------------------------------------------------------------------------------

    private static void ValidateEvents(Context c)
    {
        ReportDuplicates(c, c.Project.Events, static e => e.Path, static e => e.Id, DiagnosticCodes.DuplicateEventPath, "event path");

        foreach (AudioEvent ev in c.Project.Events)
        {
            if (!c.Buses.ContainsKey(ev.Bus))
                c.Error(DiagnosticCodes.EventBusMissing, ev.Id, $"Event \"{ev.Path}\" routes to a bus that does not exist.");

            ReportDuplicates(c, ev.LocalParameters, static p => p.Name, static p => p.Id, DiagnosticCodes.DuplicateParameterName, $"local parameter name in \"{ev.Path}\"");
            foreach (ParameterDefinition p in ev.LocalParameters) ValidateParameterDefinition(c, p);

            foreach (AutomationLane lane in ev.Automation)
            {
                RequireParameter(c, ev, lane.Parameter, lane.Id, "Automation lane");
                ValidateCurve(c, lane.Curve, lane.Id, $"Automation lane in \"{ev.Path}\"");
            }

            foreach (LogicMarker marker in ev.Markers) ValidateMarker(c, ev, marker);

            foreach (TempoMarker tempo in ev.Tempo)
                if (!(tempo.Bpm > 0f))
                    c.Error(DiagnosticCodes.TempoBpmInvalid, ev.Id, $"Event \"{ev.Path}\" has a tempo marker at {tempo.Position}s with Bpm {tempo.Bpm}; it must be > 0.");

            foreach (Track track in ev.Tracks)
                foreach (Instrument instrument in track.Instruments)
                    ValidateInstrument(c, ev, instrument);
        }
    }

    private static void ValidateCurve(Context c, Curve curve, DirgeId subject, string where)
    {
        List<CurvePoint> points = curve.Points;
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].X < points[i - 1].X)
            {
                c.Error(DiagnosticCodes.CurveUnsorted, subject, $"{where} has curve points out of X order (point {i}: {points[i].X} after {points[i - 1].X}).");
                break;
            }
        }
    }

    private static void ValidateMarker(Context c, AudioEvent ev, LogicMarker marker)
    {
        string where = $"Marker \"{marker.Name}\" in \"{ev.Path}\"";
        foreach (TriggerCondition condition in marker.Conditions)
            RequireParameter(c, ev, condition.Parameter, marker.Id, $"{where} (condition)");

        switch (marker)
        {
            case LoopRegion loop when !(loop.Length > 0f):
                c.Error(DiagnosticCodes.RegionLengthInvalid, marker.Id, $"{where}: loop region length must be > 0.");
                break;
            case TransitionRegion region:
                if (!(region.Length > 0f))
                    c.Error(DiagnosticCodes.RegionLengthInvalid, marker.Id, $"{where}: transition region length must be > 0.");
                RequireDestination(c, ev, marker, region.Destination, where);
                break;
            case TransitionMarker transition:
                RequireDestination(c, ev, marker, transition.Destination, where);
                break;
        }
    }

    private static void RequireDestination(Context c, AudioEvent ev, LogicMarker source, DirgeId destination, string where)
    {
        foreach (LogicMarker candidate in ev.Markers)
            if (candidate.Id == destination && candidate is DestinationMarker) return;
        c.Error(DiagnosticCodes.TransitionDestinationInvalid, source.Id, $"{where}: destination must be a destination marker in the same event.");
    }

    private static void ValidateInstrument(Context c, AudioEvent ev, Instrument instrument)
    {
        string where = $"Instrument \"{instrument.Name}\" in \"{ev.Path}\"";

        if (!instrument.Placement.OnTimeline)
            RequireParameter(c, ev, instrument.Placement.Parameter, instrument.Id, $"{where} (parameter sheet)");
        foreach (TriggerCondition condition in instrument.Conditions)
            RequireParameter(c, ev, condition.Parameter, instrument.Id, $"{where} (condition)");

        switch (instrument)
        {
            case ClipInstrument clip:
                if (clip.Asset.IsEmpty)
                    c.Warning(DiagnosticCodes.EmptyAssetRef, instrument.Id, $"{where} has no asset.");
                break;
            case MultiInstrument multi:
                if (multi.Entries.Count == 0)
                    c.Error(DiagnosticCodes.MultiInstrumentEmpty, instrument.Id, $"{where} has no playlist entries.");
                WarnEmptyEntries(c, instrument, multi.Entries, where);
                break;
            case ScattererInstrument scatterer:
                if (scatterer.Entries.Count == 0)
                    c.Error(DiagnosticCodes.MultiInstrumentEmpty, instrument.Id, $"{where} has no playlist entries.");
                WarnEmptyEntries(c, instrument, scatterer.Entries, where);
                break;
            case EventInstrument eventInstrument:
                if (!c.Events.ContainsKey(eventInstrument.Event))
                    c.Error(DiagnosticCodes.EventInstrumentTargetMissing, instrument.Id, $"{where} plays an event that does not exist.");
                break;
            case SnapshotInstrument snapshotInstrument:
                if (!c.Snapshots.ContainsKey(snapshotInstrument.Snapshot))
                    c.Error(DiagnosticCodes.SnapshotTargetMissing, instrument.Id, $"{where} starts a snapshot that does not exist.");
                break;
            case CommandInstrument command:
                ValidateCommand(c, ev, command, where);
                break;
        }
    }

    private static void WarnEmptyEntries(Context c, Instrument instrument, List<MultiEntry> entries, string where)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].Asset.IsEmpty)
                c.Warning(DiagnosticCodes.EmptyAssetRef, instrument.Id, $"{where} entry {i} has no asset.");
    }

    private static void ValidateCommand(Context c, AudioEvent ev, CommandInstrument command, string where)
    {
        switch (command.Action)
        {
            case CommandAction.StartEvent:
            case CommandAction.StopEvent:
                if (!c.Events.ContainsKey(command.TargetEvent))
                    c.Error(DiagnosticCodes.CommandTargetMissing, command.Id, $"{where} targets an event that does not exist.");
                break;
            case CommandAction.SetParameter:
                // Target event None = the owning event / a global parameter; otherwise the target event's locals then globals.
                AudioEvent? scope = ev;
                if (!command.TargetEvent.IsNone && !c.Events.TryGetValue(command.TargetEvent, out scope))
                {
                    c.Error(DiagnosticCodes.CommandTargetMissing, command.Id, $"{where} targets an event that does not exist.");
                    scope = null;
                }
                if (!ParameterResolves(c, scope, command.TargetParameter))
                    c.Error(DiagnosticCodes.CommandTargetMissing, command.Id, $"{where} sets a parameter that does not exist in its target scope.");
                break;
        }
    }

    // ---- event nesting -----------------------------------------------------------------------------------------

    private static void ValidateEventInstrumentCycles(Context c)
    {
        // Edges: event → event referenced by any EventInstrument. DFS with colours; every back edge is one cycle report.
        var state = new Dictionary<DirgeId, byte>(); // 1 = on stack, 2 = done
        var reported = new HashSet<DirgeId>();
        foreach (AudioEvent ev in c.Project.Events) Visit(c, ev, state, reported);
    }

    private static void Visit(Context c, AudioEvent ev, Dictionary<DirgeId, byte> state, HashSet<DirgeId> reported)
    {
        if (state.TryGetValue(ev.Id, out byte s) && s != 0) return;
        state[ev.Id] = 1;
        foreach (Track track in ev.Tracks)
        {
            foreach (Instrument instrument in track.Instruments)
            {
                if (instrument is not EventInstrument nested || !c.Events.TryGetValue(nested.Event, out AudioEvent? target)) continue;
                if (state.TryGetValue(target.Id, out byte ts) && ts == 1)
                {
                    if (reported.Add(instrument.Id))
                        c.Error(DiagnosticCodes.EventInstrumentCycle, instrument.Id, $"Instrument \"{instrument.Name}\" in \"{ev.Path}\" nests \"{target.Path}\", which (transitively) nests \"{ev.Path}\" again.");
                    continue;
                }
                Visit(c, target, state, reported);
            }
        }
        state[ev.Id] = 2;
    }
}
