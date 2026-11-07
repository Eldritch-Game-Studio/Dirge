#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Events;

/// <summary>Automation lanes (event + selected instrument) with target/parameter pickers, and modulators (Random/AHDSR/LFO).</summary>
internal sealed class AutomationView
{
    private readonly EventEditorState _state;
    private readonly EventEdits _edits;
    private readonly CurveCanvas _curve;

    public DocumentHost Host { get; }

    public AutomationView(EventEditorState state, EventEdits edits, CurveCanvas curve)
    {
        _state = state;
        _edits = edits;
        _curve = curve;
        Host = ToolDocuments.Create("event-automation.html");
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

        RenderLanes(form, root, ev, instrument);
        RenderModulators(form, root, ev, instrument);
    }

    // ---- lanes -------------------------------------------------------------------------------------------------------

    private void RenderLanes(DomForm form, Element root, AudioEvent ev, Instrument? instrument)
    {
        Element body = form.Section(root, instrument is null ? "Automation" : "Automation · " + (instrument.Name.Length > 0 ? instrument.Name : EventEdits.TypeLabel(instrument)));
        Element list = form.Div(body, "list");
        for (int i = 0; i < ev.Automation.Count; i++)
        {
            AutomationLane lane = ev.Automation[i];
            bool instrumentLane = lane.Target.Kind is PropertyKind.InstrumentVolumeDb or PropertyKind.InstrumentPitch;
            if (instrumentLane && (instrument is null || lane.Target.Owner != instrument.Id)) continue;
            RenderLane(form, list, ev, lane, instrumentLane ? instrument : null);
        }
        Element actions = form.Div(body, "actions");
        form.Span(actions, "Add lane", "label short");
        var targets = TargetOptions(ev, instrument);
        var targetNames = new string[targets.Count];
        for (int i = 0; i < targets.Count; i++) targetNames[i] = targets[i].Label;
        form.ChoiceButton(actions, targetNames, -1, i =>
        {
            PropertyRef target = targets[i].Target;
            var lane = new AutomationLane { Target = target, Parameter = DefaultParameter(ev) };
            float neutral = Neutral(target, ev);
            ParameterDefinition? p = _state.FindParameter(lane.Parameter);
            lane.Curve.Points.Add(new CurvePoint(p?.Minimum ?? 0f, neutral));
            lane.Curve.Points.Add(new CurvePoint(p?.Maximum ?? 1f, neutral));
            _edits.ListAdd("Add automation lane", ev.Automation, lane);
            _state.SelectLane(lane.Id);
        }, "target…");
    }

    private void RenderLane(DomForm form, Element list, AudioEvent ev, AutomationLane lane, Instrument? owner)
    {
        bool selected = lane.Id == _state.SelectedLane;
        Element item = form.Div(list, selected ? "item inline lane on" : "item inline lane");
        form.Button(item, _curve.TargetLabel(lane.Target, owner), () => _state.SelectLane(lane.Id), selected ? "btn lane-pick on" : "btn lane-pick");
        form.Span(item, "by", "label short");
        var (names, ids, index) = ParameterOptions(ev, lane.Parameter);
        form.ChoiceButton(item, names, index, k => _edits.Set("Lane parameter", lane.Parameter, ids[k], x => lane.Parameter = x), "(parameter)");
        form.Span(item, lane.Curve.Points.Count == 1 ? "1 point" : lane.Curve.Points.Count.ToString(DomForm.Culture) + " points", "muted");
        form.Button(item, "×", () => { _edits.ListRemove("Remove automation lane", ev.Automation, lane); if (_state.SelectedLane == lane.Id) _state.SelectLane(DirgeId.None); }, "btn small danger");
    }

    private List<(string Label, PropertyRef Target)> TargetOptions(AudioEvent ev, Instrument? instrument)
    {
        var options = new List<(string, PropertyRef)>
        {
            ("Event volume", PropertyRef.EventVolume(ev.Id)),
            ("Event pitch", PropertyRef.EventPitch(ev.Id)),
        };
        if (instrument is not null)
        {
            string name = instrument.Name.Length > 0 ? instrument.Name : EventEdits.TypeLabel(instrument);
            options.Add((name + " volume", PropertyRef.InstrumentVolume(instrument.Id)));
            options.Add((name + " pitch", PropertyRef.InstrumentPitch(instrument.Id)));
        }
        Bus? bus = _state.FindBus(ev.Bus) ?? MasterBus();
        if (bus is not null)
        {
            for (int e = 0; e < bus.Effects.Count; e++)
            {
                BusEffect effect = bus.Effects[e];
                string effectName = effect.TypeName.Replace("AudioEffect", "");
                foreach (KeyValuePair<string, float> parameter in effect.Parameters)
                    options.Add((bus.Name + " · " + effectName + " · " + parameter.Key, PropertyRef.Effect(bus.Id, e, parameter.Key)));
            }
        }
        return options;
    }

    private Bus? MasterBus()
    {
        List<Bus> buses = _state.Project.Mixer.Buses;
        for (int i = 0; i < buses.Count; i++) if (buses[i].Parent.IsNone) return buses[i];
        return null;
    }

    private float Neutral(PropertyRef target, AudioEvent ev)
    {
        switch (target.Kind)
        {
            case PropertyKind.EventPitch:
            case PropertyKind.InstrumentPitch:
            case PropertyKind.BusPitch:
                return 1f;
            case PropertyKind.EffectParameter:
                return _state.FindBus(target.Owner) is { } bus && target.EffectIndex >= 0 && target.EffectIndex < bus.Effects.Count
                       && target.Name is not null && bus.Effects[target.EffectIndex].Parameters.TryGetValue(target.Name, out float v) ? v : 0f;
            default:
                return 0f;
        }
    }

    private DirgeId DefaultParameter(AudioEvent ev)
    {
        if (!_state.Sheet.IsNone) return _state.Sheet;
        if (ev.LocalParameters.Count > 0) return ev.LocalParameters[0].Id;
        if (_state.Project.GlobalParameters.Count > 0) return _state.Project.GlobalParameters[0].Id;
        return DirgeId.None;
    }

    private (List<string> Names, List<DirgeId> Ids, int Selected) ParameterOptions(AudioEvent ev, DirgeId current)
    {
        var names = new List<string>();
        var ids = new List<DirgeId>();
        int selected = -1;
        for (int i = 0; i < ev.LocalParameters.Count; i++)
        {
            if (ev.LocalParameters[i].Id == current) selected = names.Count;
            names.Add(ev.LocalParameters[i].Name);
            ids.Add(ev.LocalParameters[i].Id);
        }
        List<ParameterDefinition> globals = _state.Project.GlobalParameters;
        for (int i = 0; i < globals.Count; i++)
        {
            if (globals[i].Id == current) selected = names.Count;
            names.Add("G: " + globals[i].Name);
            ids.Add(globals[i].Id);
        }
        return (names, ids, selected);
    }

    // ---- modulators --------------------------------------------------------------------------------------------------

    private void RenderModulators(DomForm form, Element root, AudioEvent ev, Instrument? instrument)
    {
        RenderModulatorList(form, root, "Event modulators", ev.Modulators, PropertyRef.EventVolume(ev.Id), PropertyRef.EventPitch(ev.Id));
        if (instrument is not null)
            RenderModulatorList(form, root, "Instrument modulators", instrument.Modulators, PropertyRef.InstrumentVolume(instrument.Id), PropertyRef.InstrumentPitch(instrument.Id));
    }

    private void RenderModulatorList(DomForm form, Element root, string title, List<Modulator> modulators, PropertyRef volume, PropertyRef pitch)
    {
        Element body = form.Section(root, title);
        Element list = form.Div(body, "list");
        string[] targets = { "Volume", "Pitch" };
        for (int i = 0; i < modulators.Count; i++)
        {
            Modulator mod = modulators[i];
            Element item = form.Div(list, "item grid");
            form.Span(item, mod switch { RandomModulator => "Random", AhdsrModulator => "AHDSR", LfoModulator => "LFO", _ => "Modulator" }, "type small");
            int selected = mod.Target.Kind is PropertyKind.EventPitch or PropertyKind.InstrumentPitch ? 1 : 0;
            form.Choice(item, "Target", targets, selected, k => _edits.Set("Modulator target", mod.Target, k == 1 ? pitch : volume, x => mod.Target = x));
            form.Number(item, "Amount", mod.Amount, v => _edits.Set("Modulator amount", mod.Amount, (float)v, x => mod.Amount = x), selected == 1 ? "x" : "dB");
            switch (mod)
            {
                case AhdsrModulator a:
                    Ahdsr env = a.Envelope;
                    form.Number(item, "Attack", env.Attack, v => _edits.Set("Attack", env.Attack, (float)Math.Max(0, v), x => env.Attack = x), "s");
                    form.Number(item, "Hold", env.Hold, v => _edits.Set("Hold", env.Hold, (float)Math.Max(0, v), x => env.Hold = x), "s");
                    form.Number(item, "Decay", env.Decay, v => _edits.Set("Decay", env.Decay, (float)Math.Max(0, v), x => env.Decay = x), "s");
                    form.Number(item, "Sustain", env.Sustain, v => _edits.Set("Sustain", env.Sustain, (float)Math.Clamp(v, 0, 1), x => env.Sustain = x));
                    form.Number(item, "Release", env.Release, v => _edits.Set("Release", env.Release, (float)Math.Max(0, v), x => env.Release = x), "s");
                    break;
                case LfoModulator l:
                    form.EnumChoice(item, "Shape", l.Shape, v => _edits.Set("LFO shape", l.Shape, v, x => l.Shape = x));
                    form.Number(item, l.TempoSync ? "Rate (beats)" : "Rate", l.RateHz, v => _edits.Set("LFO rate", l.RateHz, (float)Math.Max(0, v), x => l.RateHz = x), l.TempoSync ? null : "Hz");
                    form.Number(item, "Phase", l.Phase, v => _edits.Set("LFO phase", l.Phase, (float)v, x => l.Phase = x));
                    form.Toggle(item, "Tempo sync", l.TempoSync, v => _edits.Set("LFO tempo sync", l.TempoSync, v, x => l.TempoSync = x));
                    break;
            }
            form.Button(item, "Remove", () => _edits.ListRemove("Remove modulator", modulators, mod), "btn danger");
        }
        Element actions = form.Div(body, "actions");
        string[] kinds = { "Random", "AHDSR", "LFO" };
        form.Span(actions, "Add", "label short");
        form.ChoiceButton(actions, kinds, -1, k =>
        {
            Modulator mod = k switch
            {
                0 => new RandomModulator { Target = volume, Amount = 3f },
                1 => new AhdsrModulator { Target = volume, Amount = 0f, Envelope = { Attack = 0.1f, Release = 0.5f } },
                _ => new LfoModulator { Target = volume, Amount = 3f },
            };
            _edits.ListAdd("Add modulator", modulators, mod);
        }, "modulator…");
    }
}
