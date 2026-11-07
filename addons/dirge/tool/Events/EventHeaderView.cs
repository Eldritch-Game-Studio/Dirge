#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Events;

/// <summary>Event header document: transport/snap toolbar, path, bus, macros, local parameters, tempo markers.</summary>
internal sealed class EventHeaderView
{
    private readonly EventEditorState _state;
    private readonly EventEdits _edits;
    private readonly Action _playToggle;
    private readonly Action _zoomFit;
    private bool _showMacros = true;
    private bool _showParameters = true;
    private bool _showTempo = true;

    public DocumentHost Host { get; }

    public EventHeaderView(EventEditorState state, EventEdits edits, Action playToggle, Action zoomFit)
    {
        _state = state;
        _edits = edits;
        _playToggle = playToggle;
        _zoomFit = zoomFit;
        Host = ToolDocuments.Create("event-header.html");
    }

    /// <summary>True while an audition instance of the edited event runs; drives the Play/Stop label.</summary>
    public bool Playing { get; set; }

    public void Render()
    {
        Document doc = Host.Document;
        DomMenu.Close(doc);
        Element? toolbar = doc.GetElementById("toolbar");
        Element? content = doc.GetElementById("content");
        if (toolbar is null || content is null) return;
        var form = new DomForm(doc);
        toolbar.ReplaceChildren();
        content.ReplaceChildren();
        AudioEvent? ev = _state.Event;
        if (ev is null)
        {
            form.Note(content, "No event selected.");
            return;
        }

        RenderToolbar(form, toolbar, ev);
        RenderMacros(form, content, ev);
        RenderParameters(form, content, ev);
        RenderTempo(form, content, ev);
    }

    private void RenderToolbar(DomForm form, Element toolbar, AudioEvent ev)
    {
        form.Button(toolbar, Playing ? "Stop" : "Play", _playToggle, Playing ? "btn accent" : "btn");
        form.ToggleButton(toolbar, "Snap", _state.SnapToGrid, v => { _state.SnapToGrid = v; _state.Bump(); Render(); });
        form.Button(toolbar, "Fit", _zoomFit);
        form.Span(toolbar, "Event", "label");
        HTMLInputElement path = form.TextInput(toolbar, ev.Path, v =>
        {
            string trimmed = v.Trim().Trim('/');
            if (trimmed.Length == 0) { Render(); return; }
            _edits.Set("Rename event", ev.Path, trimmed, p => ev.Path = p, ChangeScope.Events);
        });
        path.ClassName = "field path";
        form.Span(toolbar, "Bus", "label");
        List<Bus> buses = _state.Project.Mixer.Buses;
        var names = new string[buses.Count];
        int selected = -1;
        for (int i = 0; i < buses.Count; i++) { names[i] = buses[i].Name; if (buses[i].Id == ev.Bus) selected = i; }
        form.ChoiceButton(toolbar, names, selected, i => _edits.Set("Route event", ev.Bus, buses[i].Id, b => ev.Bus = b), "(master)");
        form.ToggleButton(toolbar, "Conductor", ev.IsConductor, v => _edits.Set("Conductor", ev.IsConductor, v, b => ev.IsConductor = b));
        form.ToggleButton(toolbar, "Macros", _showMacros, v => { _showMacros = v; Render(); });
        form.ToggleButton(toolbar, "Parameters", _showParameters, v => { _showParameters = v; Render(); });
        form.ToggleButton(toolbar, "Tempo", _showTempo, v => { _showTempo = v; Render(); });
    }

    private void RenderMacros(DomForm form, Element content, AudioEvent ev)
    {
        if (!_showMacros) return;
        EventMacros m = ev.Macros;
        Element body = form.Section(content, "Macros");
        body.ClassName = "section-body grid";
        form.Number(body, "Volume", m.VolumeDb, v => _edits.Set("Event volume", m.VolumeDb, (float)v, x => m.VolumeDb = x), "dB");
        form.Number(body, "Pitch", m.Pitch, v => _edits.Set("Event pitch", m.Pitch, (float)Math.Max(0.01, v), x => m.Pitch = x), "x");
        form.Int(body, "Priority", m.Priority, v => _edits.Set("Event priority", m.Priority, v, x => m.Priority = x));
        form.Int(body, "Max instances", m.MaxInstances, v => _edits.Set("Max instances", m.MaxInstances, Math.Max(0, v), x => m.MaxInstances = x));
        form.EnumChoice(body, "Stealing", m.Stealing, v => _edits.Set("Stealing", m.Stealing, v, x => m.Stealing = x));
        form.Number(body, "Cooldown", m.Cooldown, v => _edits.Set("Cooldown", m.Cooldown, (float)Math.Max(0, v), x => m.Cooldown = x), "s");
        form.Toggle(body, "Persistent", m.Persistent, v => _edits.Set("Persistent", m.Persistent, v, x => m.Persistent = x));
        form.Toggle(body, "3D", m.Is3D, v => _edits.Set("3D", m.Is3D, v, x => m.Is3D = x));
        form.Number(body, "Min distance", m.MinDistance, v => _edits.Set("Min distance", m.MinDistance, (float)Math.Max(0, v), x => m.MinDistance = x), "m")
            .SetAttribute("title", "Distance under which no attenuation applies (engine unit_size).");
        form.Number(body, "Max distance", m.MaxDistance, v => _edits.Set("Max distance", m.MaxDistance, (float)Math.Max(0, v), x => m.MaxDistance = x), "m")
            .SetAttribute("title", "Distance beyond which voices are silent (engine max_distance); 0 = never.");
        form.EnumChoice(body, "Rolloff", m.Rolloff, v => _edits.Set("Rolloff", m.Rolloff, v, x => m.Rolloff = x))
            .SetAttribute("title", "Distance attenuation model (engine attenuation_model). Custom curve: choose Disabled and automate volume by the built-in Distance parameter.");
        form.Toggle(body, "Doppler", m.Doppler, v => _edits.Set("Doppler", m.Doppler, v, x => m.Doppler = x))
            .SetAttribute("title", "Pitch-shift by emitter/listener velocity (engine doppler_tracking = physics step).");
        form.Number(body, "Doppler scale", m.DopplerScale, v => _edits.Set("Doppler scale", m.DopplerScale, (float)Math.Max(0, v), x => m.DopplerScale = x))
            .SetAttribute("title", "FMOD doppler scale. Godot has no doppler scale: 0 disables doppler, any positive value enables it at engine strength; the magnitude is kept for parity only.");
        form.Number(body, "Lookahead", m.ScheduleLookahead, v => _edits.Set("Lookahead", m.ScheduleLookahead, (float)Math.Max(0, v), x => m.ScheduleLookahead = x), "s");
        form.EnumChoice(body, "Start quantization", m.StartQuantization, v => _edits.Set("Start quantization", m.StartQuantization, v, x => m.StartQuantization = x));
    }

    private void RenderParameters(DomForm form, Element content, AudioEvent ev)
    {
        if (!_showParameters) return;
        Element body = form.Section(content, "Local parameters");
        Element list = form.Div(body, "list");
        for (int i = 0; i < ev.LocalParameters.Count; i++) RenderParameter(form, list, ev, ev.LocalParameters[i]);
        Element actions = form.Div(body, "actions");
        form.Button(actions, "+ Parameter", () =>
        {
            var p = new ParameterDefinition { Name = UniqueName(ev, "Param") };
            _edits.ListAdd("Add parameter", ev.LocalParameters, p);
        });
    }

    private void RenderParameter(DomForm form, Element list, AudioEvent ev, ParameterDefinition p)
    {
        Element item = form.Div(list, "item grid");
        form.Text(item, "Name", p.Name, v => { if (v.Trim().Length > 0) _edits.Set("Rename parameter", p.Name, v.Trim(), x => p.Name = x); else Render(); });
        form.EnumChoice(item, "Kind", p.Kind, v => _edits.Set("Parameter kind", p.Kind, v, x => p.Kind = x));
        form.EnumChoice(item, "Built-in", p.BuiltIn, v => _edits.Set("Built-in source", p.BuiltIn, v, x => p.BuiltIn = x));
        form.Number(item, "Min", p.Minimum, v => _edits.Set("Parameter minimum", p.Minimum, (float)v, x => p.Minimum = x));
        form.Number(item, "Max", p.Maximum, v => _edits.Set("Parameter maximum", p.Maximum, (float)v, x => p.Maximum = x));
        form.Number(item, "Default", p.Default, v => _edits.Set("Parameter default", p.Default, (float)v, x => p.Default = x));
        form.Number(item, "Seek up", p.SeekSpeedUp, v => _edits.Set("Seek speed up", p.SeekSpeedUp, (float)Math.Max(0, v), x => p.SeekSpeedUp = x), "/s");
        form.Number(item, "Seek down", p.SeekSpeedDown, v => _edits.Set("Seek speed down", p.SeekSpeedDown, (float)Math.Max(0, v), x => p.SeekSpeedDown = x), "/s");
        form.Toggle(item, "Hold", p.Hold, v => _edits.Set("Hold", p.Hold, v, x => p.Hold = x));
        if (p.Kind == ParameterKind.Labeled)
        {
            form.Text(item, "Labels", string.Join(", ", p.Labels), v =>
            {
                var labels = new List<string>();
                foreach (string part in v.Split(',')) { string t = part.Trim(); if (t.Length > 0) labels.Add(t); }
                var old = new List<string>(p.Labels);
                _state.Document.Mutate("Parameter labels", ChangeScope.EventContent,
                    () => { p.Labels.Clear(); p.Labels.AddRange(labels); },
                    () => { p.Labels.Clear(); p.Labels.AddRange(old); }, ev.Id);
            }, "comma separated");
        }
        form.Button(item, "Remove", () => _edits.ListRemove("Remove parameter", ev.LocalParameters, p), "btn danger");
    }

    private void RenderTempo(DomForm form, Element content, AudioEvent ev)
    {
        if (!_showTempo) return;
        Element body = form.Section(content, "Tempo markers");
        Element list = form.Div(body, "list");
        for (int i = 0; i < ev.Tempo.Count; i++)
        {
            TempoMarker t = ev.Tempo[i];
            Element item = form.Div(list, "item inline");
            form.Number(item, "At", t.Position, v => _edits.SetTempoPosition(t, (float)Math.Max(0, v)), "s");
            form.Number(item, "BPM", t.Bpm, v => _edits.Set("Tempo", t.Bpm, (float)Math.Max(1, v), x => t.Bpm = x));
            form.Int(item, "Beats/bar", t.BeatsPerBar, v => _edits.Set("Time signature", t.BeatsPerBar, Math.Max(1, v), x => t.BeatsPerBar = x));
            form.Button(item, "Remove", () => _edits.RemoveTempoMarker(t), "btn danger");
        }
        if (ev.Tempo.Count == 0) form.Note(body, "No tempo: ruler shows seconds only; snapping uses 0.1 s.");
        Element actions = form.Div(body, "actions");
        form.Button(actions, "+ Tempo marker", () => _edits.AddTempoMarker(new TempoMarker
        {
            Position = (float)_state.Cursor,
            Bpm = ev.Tempo.Count > 0 ? ev.Tempo[^1].Bpm : 120f,
            BeatsPerBar = ev.Tempo.Count > 0 ? ev.Tempo[^1].BeatsPerBar : 4,
        }));
    }

    private static string UniqueName(AudioEvent ev, string stem)
    {
        for (int n = 1; ; n++)
        {
            string candidate = n == 1 ? stem : stem + n;
            bool taken = false;
            for (int i = 0; i < ev.LocalParameters.Count; i++) if (ev.LocalParameters[i].Name == candidate) { taken = true; break; }
            if (!taken) return candidate;
        }
    }
}
