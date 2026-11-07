#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Events;

/// <summary>Inspector document: forms for the selected instrument (all eight types), logic marker or track.</summary>
internal sealed class EventInspectorView
{
    private readonly EventEditorState _state;
    private readonly EventEdits _edits;
    private readonly AssetPicker _assets;

    public DocumentHost Host { get; }

    public EventInspectorView(EventEditorState state, EventEdits edits, AssetPicker assets)
    {
        _state = state;
        _edits = edits;
        _assets = assets;
        Host = ToolDocuments.Create("event-inspector.html");
    }

    public void Render()
    {
        Document doc = Host.Document;
        DomMenu.Close(doc);
        Element? root = doc.GetElementById("content");
        if (root is null) return;
        var form = new DomForm(doc);
        root.ReplaceChildren();
        AudioEvent? ev = _state.Event;
        if (ev is null) { form.Note(root, "No event."); return; }

        Instrument? instrument = _state.PrimaryInstrument;
        LogicMarker? marker = _state.PrimaryMarker;
        if (instrument is not null) { RenderInstrument(form, root, ev, instrument); return; }
        if (marker is not null) { RenderMarker(form, root, ev, marker); return; }
        if (_state.SelectedInstruments.Count > 1)
        {
            form.Note(root, $"{_state.SelectedInstruments.Count} instruments selected.");
            RenderMultiSelection(form, root);
            return;
        }
        if (_state.SelectedMarkers.Count > 1) { form.Note(root, $"{_state.SelectedMarkers.Count} markers selected."); return; }
        if (_state.FindTrack(_state.SelectedTrack) is { } track) { RenderTrack(form, root, ev, track); return; }
        form.Note(root, "Select an instrument, marker or track.");
        form.Note(root, $"{ev.Tracks.Count} tracks · {CountInstruments(ev)} instruments · {ev.Markers.Count} markers");
    }

    private static int CountInstruments(AudioEvent ev)
    {
        int n = 0;
        for (int i = 0; i < ev.Tracks.Count; i++) n += ev.Tracks[i].Instruments.Count;
        return n;
    }

    private void RenderMultiSelection(DomForm form, Element root)
    {
        Element body = form.Section(root, "Apply to all");
        var instruments = new List<Instrument>();
        for (int i = 0; i < _state.SelectedInstruments.Count; i++) if (_state.FindInstrument(_state.SelectedInstruments[i]) is { } inst) instruments.Add(inst);
        form.Number(body, "Volume", instruments.Count > 0 ? instruments[0].VolumeDb : 0, v => SetAll("Volume", instruments, i => i.VolumeDb, (i, x) => i.VolumeDb = x, (float)v), "dB");
        form.Number(body, "Pitch", instruments.Count > 0 ? instruments[0].Pitch : 1, v => SetAll("Pitch", instruments, i => i.Pitch, (i, x) => i.Pitch = x, (float)Math.Max(0.01, v)), "x");
    }

    private void SetAll(string label, List<Instrument> instruments, Func<Instrument, float> get, Action<Instrument, float> set, float value)
    {
        var old = new float[instruments.Count];
        for (int i = 0; i < instruments.Count; i++) old[i] = get(instruments[i]);
        _state.Document.Mutate(label, ChangeScope.EventContent,
            () => { for (int i = 0; i < instruments.Count; i++) set(instruments[i], value); },
            () => { for (int i = 0; i < instruments.Count; i++) set(instruments[i], old[i]); }, _state.EventId);
    }

    // ---- instruments -------------------------------------------------------------------------------------------------

    private void RenderInstrument(DomForm form, Element root, AudioEvent ev, Instrument inst)
    {
        Element head = form.Div(root, "head");
        form.Span(head, EventEdits.TypeLabel(inst), "type " + EventEdits.TypeLabel(inst).ToLowerInvariant());
        HTMLInputElement name = form.TextInput(head, inst.Name, v => _edits.Set("Rename instrument", inst.Name, v.Trim(), x => inst.Name = x), "name");
        name.ClassName = "field name";

        RenderTypeSection(form, root, ev, inst);
        RenderPlacement(form, root, inst);
        RenderCommon(form, root, inst);
        RenderConditions(form, root, ev, inst.Conditions, "Conditions");
    }

    private void RenderTypeSection(DomForm form, Element root, AudioEvent ev, Instrument inst)
    {
        switch (inst)
        {
            case ClipInstrument clip:
            {
                Element body = form.Section(root, "Clip");
                Element row = form.Row(body, "Asset");
                HTMLInputElement asset = form.TextInput(row, clip.Asset.Path, v => _edits.Set("Clip asset", clip.Asset, new AssetRef(v.Trim()), a => clip.Asset = a), "res://…");
                asset.ClassName = "field grow";
                form.Button(row, "Browse", () => _assets.Pick(clip.Asset.Path, p => _edits.Set("Clip asset", clip.Asset, new AssetRef(p), a => clip.Asset = a)));
                break;
            }
            case MultiInstrument multi:
            {
                Element body = form.Section(root, "Multi");
                form.EnumChoice(body, "Mode", multi.Mode, v => _edits.Set("Playlist mode", multi.Mode, v, x => multi.Mode = x));
                form.Int(body, "Avoid repeat", multi.AvoidRepeat, v => _edits.Set("Avoid repeat", multi.AvoidRepeat, Math.Max(0, v), x => multi.AvoidRepeat = x));
                RenderEntries(form, body, multi.Entries);
                break;
            }
            case ScattererInstrument scatterer:
            {
                Element body = form.Section(root, "Scatterer");
                form.Number(body, "Min interval", scatterer.MinInterval, v => _edits.Set("Min interval", scatterer.MinInterval, (float)Math.Max(0, v), x => scatterer.MinInterval = x), "s");
                form.Number(body, "Max interval", scatterer.MaxInterval, v => _edits.Set("Max interval", scatterer.MaxInterval, (float)Math.Max(0, v), x => scatterer.MaxInterval = x), "s");
                form.Number(body, "Min distance", scatterer.MinScatterDistance, v => _edits.Set("Min scatter distance", scatterer.MinScatterDistance, (float)Math.Max(0, v), x => scatterer.MinScatterDistance = x), "m");
                form.Number(body, "Max distance", scatterer.MaxScatterDistance, v => _edits.Set("Max scatter distance", scatterer.MaxScatterDistance, (float)Math.Max(0, v), x => scatterer.MaxScatterDistance = x), "m");
                form.EnumChoice(body, "Mode", scatterer.Mode, v => _edits.Set("Playlist mode", scatterer.Mode, v, x => scatterer.Mode = x));
                RenderEntries(form, body, scatterer.Entries);
                break;
            }
            case EventInstrument nested:
            {
                Element body = form.Section(root, "Event");
                List<AudioEvent> events = _state.Project.Events;
                var names = new List<string>();
                var ids = new List<DirgeId>();
                int selected = -1;
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i].Id == ev.Id) continue;
                    if (events[i].Id == nested.Event) selected = names.Count;
                    names.Add(events[i].Path);
                    ids.Add(events[i].Id);
                }
                form.Choice(body, "Event", names, selected, i => _edits.Set("Referenced event", nested.Event, ids[i], x => nested.Event = x));
                break;
            }
            case ProgrammerInstrument programmer:
            {
                Element body = form.Section(root, "Programmer");
                form.Text(body, "Key", programmer.Key, v => _edits.Set("Programmer key", programmer.Key, v.Trim(), x => programmer.Key = x), "dialogue key");
                form.Note(body, "Resolved through audio tables, then the ProgrammerSound callback.");
                break;
            }
            case CommandInstrument command:
            {
                Element body = form.Section(root, "Command");
                form.EnumChoice(body, "Action", command.Action, v => _edits.Set("Command action", command.Action, v, x => command.Action = x));
                List<AudioEvent> events = _state.Project.Events;
                var names = new string[events.Count];
                int selected = -1;
                for (int i = 0; i < events.Count; i++) { names[i] = events[i].Path; if (events[i].Id == command.TargetEvent) selected = i; }
                form.Choice(body, "Target event", names, selected, i => _edits.Set("Command target", command.TargetEvent, events[i].Id, x => command.TargetEvent = x));
                if (command.Action == CommandAction.SetParameter)
                {
                    var (paramNames, paramIds, paramSelected) = ParameterOptions(ev, command.TargetParameter, includeGlobal: true, includeEvent: _state.FindEvent(command.TargetEvent));
                    form.Choice(body, "Parameter", paramNames, paramSelected, i => _edits.Set("Command parameter", command.TargetParameter, paramIds[i], x => command.TargetParameter = x));
                    form.Number(body, "Value", command.Value, v => _edits.Set("Command value", command.Value, (float)v, x => command.Value = x));
                }
                break;
            }
            case SnapshotInstrument snapshot:
            {
                Element body = form.Section(root, "Snapshot");
                List<Snapshot> snapshots = _state.Project.Snapshots;
                var names = new string[snapshots.Count];
                int selected = -1;
                for (int i = 0; i < snapshots.Count; i++) { names[i] = snapshots[i].Name; if (snapshots[i].Id == snapshot.Snapshot) selected = i; }
                form.Choice(body, "Snapshot", names, selected, i => _edits.Set("Snapshot", snapshot.Snapshot, snapshots[i].Id, x => snapshot.Snapshot = x));
                form.Note(body, "Started while the region is active; stopped on exit.");
                break;
            }
            case SilenceInstrument:
            {
                Element body = form.Section(root, "Silence");
                form.Note(body, "Plays nothing for its length; useful in multi/sequence gaps and parameter sheets.");
                break;
            }
        }
    }

    private void RenderEntries(DomForm form, Element body, List<MultiEntry> entries)
    {
        Element list = form.Div(body, "list");
        for (int i = 0; i < entries.Count; i++)
        {
            MultiEntry entry = entries[i];
            Element item = form.Div(list, "item inline");
            HTMLInputElement asset = form.TextInput(item, entry.Asset.Path, v => _edits.Set("Entry asset", entry.Asset, new AssetRef(v.Trim()), a => entry.Asset = a), "res://…");
            asset.ClassName = "field grow";
            form.Button(item, "…", () => _assets.Pick(entry.Asset.Path, p => _edits.Set("Entry asset", entry.Asset, new AssetRef(p), a => entry.Asset = a)), "btn small");
            form.Span(item, "w", "label short");
            form.NumberInput(item, entry.Weight, v => _edits.Set("Entry weight", entry.Weight, (float)Math.Max(0, v), w => entry.Weight = w));
            form.Button(item, "×", () => _edits.ListRemove("Remove entry", entries, entry), "btn small danger");
        }
        Element actions = form.Div(body, "actions");
        form.Button(actions, "+ Entry", () => _edits.ListAdd("Add entry", entries, new MultiEntry()));
        form.Button(actions, "+ Browse…", () => _assets.Pick("", p => _edits.ListAdd("Add entry", entries, new MultiEntry { Asset = new AssetRef(p) })));
    }

    private void RenderPlacement(DomForm form, Element root, Instrument inst)
    {
        Placement p = inst.Placement;
        Element body = form.Section(root, p.OnTimeline ? "Placement (timeline)" : "Placement (" + _state.ParameterName(p.Parameter) + ")");
        body.ClassName = "section-body grid";
        form.Number(body, "Start", p.Start, v => _edits.Set("Start", p.Start, (float)v, x => p.Start = x), p.OnTimeline ? "s" : null);
        form.Number(body, "Length", p.Length, v => _edits.Set("Length", p.Length, (float)Math.Max(0, v), x => p.Length = x), p.OnTimeline ? "s (0 = natural)" : null);
        form.Int(body, "Sync group", inst.SyncGroup, v => _edits.Set("Sync group", inst.SyncGroup, Math.Max(0, v), x => inst.SyncGroup = x));
    }

    private void RenderCommon(DomForm form, Element root, Instrument inst)
    {
        Element body = form.Section(root, "Instrument");
        body.ClassName = "section-body grid";
        form.Number(body, "Volume", inst.VolumeDb, v => _edits.Set("Volume", inst.VolumeDb, (float)v, x => inst.VolumeDb = x), "dB");
        form.Number(body, "Pitch", inst.Pitch, v => _edits.Set("Pitch", inst.Pitch, (float)Math.Max(0.01, v), x => inst.Pitch = x), "x");
        form.Number(body, "Volume random", inst.VolumeRandomDb, v => _edits.Set("Volume random", inst.VolumeRandomDb, (float)Math.Max(0, v), x => inst.VolumeRandomDb = x), "dB");
        form.Number(body, "Pitch random", inst.PitchRandom, v => _edits.Set("Pitch random", inst.PitchRandom, (float)Math.Max(0, v), x => inst.PitchRandom = x));
        form.Number(body, "Start offset", inst.StartOffset, v => _edits.Set("Start offset", inst.StartOffset, (float)Math.Max(0, v), x => inst.StartOffset = x), "s");
        form.Number(body, "Fade in", inst.FadeIn, v => _edits.Set("Fade in", inst.FadeIn, (float)Math.Max(0, v), x => inst.FadeIn = x), "s");
        form.Number(body, "Fade out", inst.FadeOut, v => _edits.Set("Fade out", inst.FadeOut, (float)Math.Max(0, v), x => inst.FadeOut = x), "s");
        form.Toggle(body, "Loop", inst.Loop, v => _edits.Set("Loop", inst.Loop, v, x => inst.Loop = x));
        form.Toggle(body, "Async", inst.Async, v => _edits.Set("Async", inst.Async, v, x => inst.Async = x));
        form.EnumChoice(body, "Cut mode", inst.CutMode, v => _edits.Set("Cut mode", inst.CutMode, v, x => inst.CutMode = x));
        form.Int(body, "Polyphony", inst.Polyphony, v => _edits.Set("Polyphony", inst.Polyphony, Math.Max(0, v), x => inst.Polyphony = x));
        form.Number(body, "Trigger delay", inst.TriggerDelay, v => _edits.Set("Trigger delay", inst.TriggerDelay, (float)Math.Max(0, v), x => inst.TriggerDelay = x), "s");
        form.Number(body, "Probability", inst.Probability, v => _edits.Set("Probability", inst.Probability, (float)Math.Clamp(v, 0, 1), x => inst.Probability = x));
    }

    private void RenderConditions(DomForm form, Element root, AudioEvent ev, List<TriggerCondition> conditions, string title)
    {
        Element body = form.Section(root, title);
        Element list = form.Div(body, "list");
        for (int i = 0; i < conditions.Count; i++)
        {
            TriggerCondition c = conditions[i];
            Element item = form.Div(list, "item inline");
            var (names, ids, selected) = ParameterOptions(ev, c.Parameter, includeGlobal: true, includeEvent: null);
            form.ChoiceButton(item, names, selected, k => _edits.Set("Condition parameter", c.Parameter, ids[k], x => c.Parameter = x), "(parameter)");
            form.Span(item, "min", "label short");
            form.NumberInput(item, c.Minimum, v => _edits.Set("Condition minimum", c.Minimum, (float)v, x => c.Minimum = x));
            form.Span(item, "max", "label short");
            form.NumberInput(item, c.Maximum, v => _edits.Set("Condition maximum", c.Maximum, (float)v, x => c.Maximum = x));
            form.Button(item, "×", () => _edits.ListRemove("Remove condition", conditions, c), "btn small danger");
        }
        Element actions = form.Div(body, "actions");
        form.Button(actions, "+ Condition", () =>
        {
            var c = new TriggerCondition { Maximum = 1f };
            if (ev.LocalParameters.Count > 0) { c.Parameter = ev.LocalParameters[0].Id; c.Minimum = ev.LocalParameters[0].Minimum; c.Maximum = ev.LocalParameters[0].Maximum; }
            else if (_state.Project.GlobalParameters.Count > 0) { var g = _state.Project.GlobalParameters[0]; c.Parameter = g.Id; c.Minimum = g.Minimum; c.Maximum = g.Maximum; }
            _edits.ListAdd("Add condition", conditions, c);
        });
    }

    /// <summary>Local parameters of <paramref name="ev"/> (and optionally of another event), then globals.</summary>
    private (List<string> Names, List<DirgeId> Ids, int Selected) ParameterOptions(AudioEvent ev, DirgeId current, bool includeGlobal, AudioEvent? includeEvent)
    {
        var names = new List<string>();
        var ids = new List<DirgeId>();
        int selected = -1;
        void Add(ParameterDefinition p, string prefix)
        {
            if (p.Id == current) selected = names.Count;
            names.Add(prefix + p.Name);
            ids.Add(p.Id);
        }
        for (int i = 0; i < ev.LocalParameters.Count; i++) Add(ev.LocalParameters[i], "");
        if (includeEvent is not null && !ReferenceEquals(includeEvent, ev))
            for (int i = 0; i < includeEvent.LocalParameters.Count; i++) Add(includeEvent.LocalParameters[i], includeEvent.Path + "/");
        if (includeGlobal)
            for (int i = 0; i < _state.Project.GlobalParameters.Count; i++) Add(_state.Project.GlobalParameters[i], "G: ");
        return (names, ids, selected);
    }

    // ---- markers -----------------------------------------------------------------------------------------------------

    private void RenderMarker(DomForm form, Element root, AudioEvent ev, LogicMarker marker)
    {
        Element head = form.Div(root, "head");
        form.Span(head, EventEdits.MarkerLabel(marker), "type marker");
        HTMLInputElement name = form.TextInput(head, marker.Name, v => _edits.Set("Rename marker", marker.Name, v.Trim(), x => marker.Name = x), "name");
        name.ClassName = "field name";

        Element body = form.Section(root, "Marker");
        body.ClassName = "section-body grid";
        form.Number(body, "Position", marker.Position, v => _edits.Set("Marker position", marker.Position, (float)Math.Max(0, v), x => marker.Position = x), "s");
        if (EventEdits.IsRegion(marker))
            form.Number(body, "Length", EventEdits.RegionLength(marker), v => _edits.Set("Region length", EventEdits.RegionLength(marker), (float)Math.Max(0.01, v), x => EventEdits.SetRegionLength(marker, x)), "s");
        form.Number(body, "Probability", marker.Probability, v => _edits.Set("Marker probability", marker.Probability, (float)Math.Clamp(v, 0, 1), x => marker.Probability = x));

        switch (marker)
        {
            case TransitionMarker t:
                RenderDestination(form, body, ev, t.Destination, d => _edits.Set("Destination", t.Destination, d, x => t.Destination = x));
                form.EnumChoice(body, "Quantization", t.Quantization, v => _edits.Set("Quantization", t.Quantization, v, x => t.Quantization = x));
                form.Number(body, "Transition length", t.TransitionLength, v => _edits.Set("Transition length", t.TransitionLength, (float)Math.Max(0, v), x => t.TransitionLength = x), "s");
                RenderTransitionInstruments(form, root, t.TransitionInstruments);
                break;
            case TransitionRegion r:
                RenderDestination(form, body, ev, r.Destination, d => _edits.Set("Destination", r.Destination, d, x => r.Destination = x));
                form.EnumChoice(body, "Quantization", r.Quantization, v => _edits.Set("Quantization", r.Quantization, v, x => r.Quantization = x));
                form.Number(body, "Transition length", r.TransitionLength, v => _edits.Set("Transition length", r.TransitionLength, (float)Math.Max(0, v), x => r.TransitionLength = x), "s");
                RenderTransitionInstruments(form, root, r.TransitionInstruments);
                break;
            case LoopRegion:
                form.Note(body, "Timeline wraps to the region start when it reaches the end and the conditions pass.");
                break;
            case SustainPoint:
                form.Note(body, "Timeline holds here until KeyOff.");
                break;
            case DestinationMarker:
                form.Note(body, "Jump target for transitions; raises a marker callback when reached.");
                break;
            case MagnetRegion:
                form.Note(body, "Seeks landing inside the region snap to its start.");
                break;
        }
        RenderConditions(form, root, ev, marker.Conditions, "Conditions");
    }

    private void RenderDestination(DomForm form, Element body, AudioEvent ev, DirgeId current, Action<DirgeId> commit)
    {
        var names = new List<string>();
        var ids = new List<DirgeId>();
        int selected = -1;
        for (int i = 0; i < ev.Markers.Count; i++)
        {
            LogicMarker m = ev.Markers[i];
            if (m is TransitionMarker or TransitionRegion) continue;
            if (m.Id == current) selected = names.Count;
            names.Add((m.Name.Length > 0 ? m.Name : EventEdits.MarkerLabel(m)) + " @ " + DomForm.F(m.Position) + " s");
            ids.Add(m.Id);
        }
        form.Choice(body, "Destination", names, selected, i => commit(ids[i]));
    }

    private void RenderTransitionInstruments(DomForm form, Element root, List<Instrument> instruments)
    {
        Element body = form.Section(root, "Transition timeline");
        Element list = form.Div(body, "list");
        for (int i = 0; i < instruments.Count; i++)
        {
            Instrument inst = instruments[i];
            Element item = form.Div(list, "item inline");
            form.Span(item, EventEdits.TypeLabel(inst), "type small " + EventEdits.TypeLabel(inst).ToLowerInvariant());
            HTMLInputElement name = form.TextInput(item, inst.Name, v => _edits.Set("Rename instrument", inst.Name, v.Trim(), x => inst.Name = x), "name");
            name.ClassName = "field";
            if (inst is ClipInstrument clip)
            {
                HTMLInputElement asset = form.TextInput(item, clip.Asset.Path, v => _edits.Set("Clip asset", clip.Asset, new AssetRef(v.Trim()), a => clip.Asset = a), "res://…");
                asset.ClassName = "field grow";
                form.Button(item, "…", () => _assets.Pick(clip.Asset.Path, p => _edits.Set("Clip asset", clip.Asset, new AssetRef(p), a => clip.Asset = a)), "btn small");
            }
            form.Span(item, "dB", "label short");
            form.NumberInput(item, inst.VolumeDb, v => _edits.Set("Volume", inst.VolumeDb, (float)v, x => inst.VolumeDb = x));
            form.Button(item, "×", () => _edits.ListRemove("Remove transition instrument", instruments, inst), "btn small danger");
        }
        Element actions = form.Div(body, "actions");
        string[] kinds = { "Clip", "Multi", "Silence" };
        form.Span(actions, "Add", "label short");
        form.ChoiceButton(actions, kinds, -1, k =>
        {
            Instrument inst = k switch
            {
                0 => new ClipInstrument { Name = "Transition clip" },
                1 => new MultiInstrument { Name = "Transition multi" },
                _ => new SilenceInstrument { Name = "Gap", Placement = { Length = 1f } },
            };
            _edits.ListAdd("Add transition instrument", instruments, inst);
        }, "instrument…");
    }

    // ---- tracks ------------------------------------------------------------------------------------------------------

    private void RenderTrack(DomForm form, Element root, AudioEvent ev, Track track)
    {
        Element head = form.Div(root, "head");
        form.Span(head, "Track", "type track");
        HTMLInputElement name = form.TextInput(head, track.Name, v => _edits.Set("Rename track", track.Name, v.Trim(), x => track.Name = x), "name");
        name.ClassName = "field name";
        Element body = form.Section(root, "Track");
        form.Number(body, "Volume", track.VolumeDb, v => _edits.Set("Track volume", track.VolumeDb, (float)v, x => track.VolumeDb = x), "dB");
        form.Note(body, $"{track.Instruments.Count} instruments");
        Element actions = form.Div(body, "actions");
        form.Button(actions, "Remove track", () => { _edits.RemoveTrack(track); _state.Refresh(); }, "btn danger");
        int index = ev.Tracks.IndexOf(track);
        if (index > 0) form.Button(actions, "Move up", () => MoveTrack(ev, track, index - 1));
        if (index >= 0 && index < ev.Tracks.Count - 1) form.Button(actions, "Move down", () => MoveTrack(ev, track, index + 1));
    }

    private void MoveTrack(AudioEvent ev, Track track, int newIndex)
    {
        int oldIndex = ev.Tracks.IndexOf(track);
        if (oldIndex < 0) return;
        _state.Document.Mutate("Reorder tracks", ChangeScope.EventContent,
            () => { ev.Tracks.Remove(track); ev.Tracks.Insert(Math.Clamp(newIndex, 0, ev.Tracks.Count), track); },
            () => { ev.Tracks.Remove(track); ev.Tracks.Insert(Math.Clamp(oldIndex, 0, ev.Tracks.Count), track); }, ev.Id);
    }
}
