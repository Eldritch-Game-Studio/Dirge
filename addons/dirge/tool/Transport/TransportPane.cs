#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Browser;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Paint;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Transport;

/// <summary>
/// Editor transport: play/stop/stop-all for the selected event, loop (one-shots restart when they stop), the tool's running
/// instances, global and selected-event parameter sliders, readouts, and the <see cref="PreviewPanner"/> driving
/// <see cref="IAudition.PreviewEmitter"/>/<see cref="IAudition.PreviewListener"/>.
/// </summary>
public sealed partial class TransportPane : IToolPane, IToolPaneCommands
{
    private const double RefreshInterval = 0.1;

    private readonly TransportRoot _root = new() { Name = "Transport" };
    private PreviewPanner _panner = null!;
    private readonly List<EventInstance> _shown = new();
    private readonly List<InstanceRow> _instanceRows = new();
    private readonly List<SliderRow> _sliders = new();
    private readonly Dictionary<DirgeId, Dictionary<string, float>> _localValues = new();
    private readonly List<DirgeId> _restart = new();
    private readonly HashSet<EventInstance> _userStopped = new(ReferenceEqualityComparer.Instance);
    private readonly Action<EventInstance, CallbackEvent> _onCallback;

    private ToolContext? _ctx;
    private DocumentHost? _host;
    private Document? _doc;
    private HTMLElement _play = null!, _stop = null!, _stopAll = null!, _loop = null!, _eventName = null!, _localHeading = null!, _ddPopup = null!;
    private Element _roInstances = null!, _roVoices = null!, _roMs = null!, _globalList = null!, _localList = null!, _instanceList = null!;
    private DirgeId _selectedEvent;
    private bool _loopOn;
    private double _accum;
    private SliderRow? _activeSlider;
    private SliderRow? _dropdownFor;
    private int _lastInstances = -1, _lastVoices = -1;
    private double _lastMs = double.NaN;

    public string Title => "Transport";
    public Control Root => _root;
    public PreviewPanner Panner => _panner;
    public DocumentHost Host => _host ?? throw new InvalidOperationException("Transport is not attached.");
    public bool Loop => _loopOn;
    public DirgeId SelectedEvent => _selectedEvent;

    public TransportPane()
    {
        _onCallback = OnInstanceCallback;
    }

    public void Attach(ToolContext context)
    {
        _ctx = context;
        _host = ToolDocuments.Create("transport.html");
        _host.Name = "Document";
        _root.AddChild(ToolDocuments.Slot(_host, "DocumentSlot", new Vector2(360, 0)));
        _panner = new PreviewPanner { SizeFlagsHorizontal = Control.SizeFlags.Fill };
        ToolDocuments.SetAuthoredMinimumSize(_panner, new Vector2(220, 150));
        _root.AddChild(_panner);
        ToolScale.Changed += OnScaleChanged;
        _root.SplitOffsets = new[] { 0 };
        _root.Processed += Tick;
        _panner.Changed += OnPannerChanged;

        BindDocument(_host.Document ?? throw new InvalidOperationException("ToolDocuments.Create did not load transport.html."));

        context.Project.Changed += OnProjectChanged;
        context.Selection.Changed += OnSelectionChanged;
        context.Audition.Rebuilt += OnRebuilt;
        _panner.Emitter = context.Audition.PreviewEmitter;
        _panner.Listener = context.Audition.PreviewListener;
        OnSelectionChanged();
        RebuildParameters();
        RefreshInstances(force: true);
    }

    public void Detach()
    {
        if (_ctx is null) return;
        _ctx.Project.Changed -= OnProjectChanged;
        _ctx.Selection.Changed -= OnSelectionChanged;
        _ctx.Audition.Rebuilt -= OnRebuilt;
        _root.Processed -= Tick;
        ToolScale.Changed -= OnScaleChanged;
        _panner.Changed -= OnPannerChanged;
        foreach (Godot.Node child in _root.GetChildren()) { _root.RemoveChild(child); child.QueueFree(); }
        _host = null;
        _doc = null;
        _sliders.Clear();
        _instanceRows.Clear();
        _shown.Clear();
        _restart.Clear();
        _userStopped.Clear();
        _ctx = null;
    }

    private void OnScaleChanged() => ToolDocuments.ApplyMinimumSizes(_root);

    // ---- document -------------------------------------------------------------------------------------------------------

    private void BindDocument(Document doc)
    {
        _doc = doc;
        _play = (HTMLElement)doc.GetElementById("play")!;
        _stop = (HTMLElement)doc.GetElementById("stop")!;
        _stopAll = (HTMLElement)doc.GetElementById("stop-all")!;
        _loop = (HTMLElement)doc.GetElementById("loop")!;
        _eventName = (HTMLElement)doc.GetElementById("event-name")!;
        _localHeading = (HTMLElement)doc.GetElementById("local-heading")!;
        _ddPopup = (HTMLElement)doc.GetElementById("dd-popup")!;
        _roInstances = doc.GetElementById("ro-instances")!;
        _roVoices = doc.GetElementById("ro-voices")!;
        _roMs = doc.GetElementById("ro-ms")!;
        _globalList = doc.GetElementById("global-params")!;
        _localList = doc.GetElementById("local-params")!;
        _instanceList = doc.GetElementById("instance-list")!;

        Dom.On(_play, "click", _ => Play());
        Dom.On(_stop, "click", _ => StopSelected());
        Dom.On(_stopAll, "click", _ => StopAll());
        Dom.On(_loop, "click", _ => SetLoop(!_loopOn));
        Dom.On(_instanceList, "click", OnInstanceClick);

        Element paramsSection = doc.QuerySelector(".params")!;
        Dom.On(paramsSection, "pointerdown", OnParamPointerDown);
        Dom.On(paramsSection, "pointermove", OnParamPointerMove);
        Dom.On(paramsSection, "pointerup", OnParamPointerUp);
        Dom.On(paramsSection, "click", OnParamClick);
        Dom.On(_ddPopup, "click", OnDropdownItem);
        Dom.On(doc, "pointerdown", OnDocumentPointerDown);
    }

    private void OnProjectChanged(ChangeScope scope, DirgeId subject)
    {
        if ((scope & (ChangeScope.GlobalParameters | ChangeScope.Events | ChangeScope.EventContent)) != 0 || scope == ChangeScope.All)
        {
            if (scope == ChangeScope.All) _localValues.Clear();
            OnSelectionChanged();
            RebuildParameters();
        }
    }

    private void OnRebuilt()
    {
        _restart.Clear();
        _userStopped.Clear();
        _shown.Clear();
        RefreshInstances(force: true);
        RebuildParameters();
    }

    private void OnSelectionChanged()
    {
        if (_ctx is null) return;
        Selection s = _ctx.Selection;
        DirgeId candidate = s.Kind switch
        {
            SelectionKind.Event => s.Id,
            SelectionKind.Instrument or SelectionKind.Track or SelectionKind.Marker => s.Owner,
            SelectionKind.Parameter when !s.Owner.IsNone => s.Owner,
            _ => _selectedEvent,
        };
        AudioEvent? ev = FindEvent(candidate);
        if (ev is null && !candidate.IsNone) { candidate = DirgeId.None; }
        bool changed = candidate != _selectedEvent;
        _selectedEvent = candidate;
        if (ev is null)
        {
            Dom.SetText(_eventName, "No event selected");
            Dom.SetClass(_eventName, "none", true);
            _panner.SetDistances(1f, 20f);
        }
        else
        {
            Dom.SetText(_eventName, ev.Path + (ev.Macros.Is3D ? "  ·  3D" : ""));
            Dom.SetClass(_eventName, "none", false);
            _panner.SetDistances(ev.Macros.MinDistance, ev.Macros.MaxDistance);
        }
        ((HTMLElement)_play).ToggleAttribute("disabled", ev is null);
        ((HTMLElement)_stop).ToggleAttribute("disabled", ev is null);
        if (changed) RebuildParameters();
    }

    private AudioEvent? FindEvent(DirgeId id)
    {
        if (_ctx is null || id.IsNone) return null;
        List<AudioEvent> events = _ctx.Project.Project.Events;
        for (int i = 0; i < events.Count; i++) if (events[i].Id == id) return events[i];
        return null;
    }

    // ---- playback -------------------------------------------------------------------------------------------------------

    public EventInstance? Play()
    {
        if (_ctx is null || _selectedEvent.IsNone) return null;
        return PlayEvent(_selectedEvent);
    }

    private EventInstance? PlayEvent(DirgeId id)
    {
        if (_ctx is null) return null;
        EventInstance? instance = _ctx.Audition.Play(id);
        if (instance is null) return null;
        instance.Callback += _onCallback;
        if (_localValues.TryGetValue(id, out Dictionary<string, float>? values))
        {
            foreach (KeyValuePair<string, float> pair in values) instance.SetParameter(pair.Key, pair.Value, immediate: true);
        }
        RefreshInstances(force: true);
        return instance;
    }

    private void OnInstanceCallback(EventInstance instance, CallbackEvent callback)
    {
        if (callback is not StoppedCallback) return;
        instance.Callback -= _onCallback;
        bool userStopped = _userStopped.Remove(instance);
        if (_loopOn && !userStopped && _ctx is not null) _restart.Add(instance.Description.Id);
    }

    public void StopSelected()
    {
        if (_ctx is null) return;
        IReadOnlyList<EventInstance> instances = _ctx.Audition.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Description.Id != _selectedEvent) continue;
            StopInstance(instances[i]);
        }
    }

    public void StopInstance(EventInstance instance)
    {
        _userStopped.Add(instance);
        instance.Stop(StopMode.AllowFadeout);
    }

    public void StopAll()
    {
        if (_ctx is null) return;
        _restart.Clear();
        IReadOnlyList<EventInstance> instances = _ctx.Audition.Instances;
        for (int i = 0; i < instances.Count; i++) _userStopped.Add(instances[i]);
        _ctx.Audition.StopAll(StopMode.AllowFadeout);
    }

    public void SetLoop(bool on)
    {
        _loopOn = on;
        Dom.SetClass(_loop, "on", on);
        if (!on) _restart.Clear();
    }

    private bool SelectedIsPlaying()
    {
        if (_ctx is null) return false;
        IReadOnlyList<EventInstance> instances = _ctx.Audition.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Description.Id == _selectedEvent && instances[i].State != PlaybackState.Stopped) return true;
        }
        return false;
    }

    public bool Handle(ToolCommand command)
    {
        switch (command)
        {
            case ToolCommand.PlayStop:
                if (_selectedEvent.IsNone) return false;
                if (SelectedIsPlaying()) StopSelected(); else Play();
                return true;
            case ToolCommand.Escape:
                if (_ddPopup.Hidden) return false;
                HideDropdown();
                return true;
            default:
                return false;
        }
    }

    // ---- per-frame -------------------------------------------------------------------------------------------------------

    private void Tick(double delta)
    {
        if (_ctx is null) return;
        if (_restart.Count > 0)
        {
            for (int i = 0; i < _restart.Count; i++) PlayEvent(_restart[i]);
            _restart.Clear();
        }
        _accum += delta;
        if (_accum < RefreshInterval) return;
        _accum = 0;
        RefreshInstances(force: false);
        RefreshReadouts();
        RefreshSliderValues();
    }

    private void RefreshReadouts()
    {
        DirgeSystem? system = _ctx!.Audition.System;
        int instances = system?.InstanceCount ?? 0;
        int voices = system?.RealVoiceCount ?? 0;
        // 0.1 ms steps: the readout is idle-stable and every change is a same-width digit swap (no relayout).
        double ms = Math.Round(_ctx.Audition.LastUpdateMs, 1);
        if (instances != _lastInstances) { _lastInstances = instances; _roInstances.TextContent = instances.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        if (voices != _lastVoices) { _lastVoices = voices; _roVoices.TextContent = voices.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        if (ms != _lastMs) { _lastMs = ms; _roMs.TextContent = Dom.F(ms, 1) + " ms"; }
    }

    // ---- instances ---------------------------------------------------------------------------------------------------------

    private sealed class InstanceRow
    {
        public InstanceRow(Element row, Element state, Element position) { Row = row; State = state; Position = position; }
        public Element Row { get; }
        public Element State { get; }
        public Element Position { get; }
        public PlaybackState LastState { get; set; } = (PlaybackState)(-1);
        public bool LastVirtual { get; set; }
    }

    private void RefreshInstances(bool force)
    {
        if (_ctx is null || _doc is null) return;
        IReadOnlyList<EventInstance> live = _ctx.Audition.Instances;
        bool same = !force && live.Count == _shown.Count;
        if (same)
        {
            for (int i = 0; i < live.Count; i++) if (!ReferenceEquals(live[i], _shown[i])) { same = false; break; }
        }
        if (!same)
        {
            _shown.Clear();
            _instanceRows.Clear();
            while (_instanceList.FirstChild is { } child) _instanceList.RemoveChild(child);
            for (int i = 0; i < live.Count; i++)
            {
                EventInstance instance = live[i];
                _shown.Add(instance);
                HTMLElement row = Dom.Add(_instanceList, "div", "inst");
                row.SetAttribute("data-index", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Dom.Add(row, "p", "iname", instance.Description.Path);
                HTMLElement state = Dom.Add(row, "p", "istate", "");
                HTMLElement position = Dom.Add(row, "p", "ipos", "");
                Dom.Add(row, "button", "istop", "Stop");
                _instanceRows.Add(new InstanceRow(row, state, position));
            }
            if (live.Count == 0) Dom.Add(_instanceList, "p", "empty", "Nothing playing — select an event and press Play");
        }
        for (int i = 0; i < _instanceRows.Count; i++)
        {
            InstanceRow row = _instanceRows[i];
            EventInstance instance = _shown[i];
            if (instance.State != row.LastState || instance.IsVirtual != row.LastVirtual)
            {
                row.LastState = instance.State;
                row.LastVirtual = instance.IsVirtual;
                row.State.TextContent = instance.IsVirtual ? instance.State + " (virtual)" : instance.State.ToString();
                Dom.SetClass(row.State, "playing", instance.State == PlaybackState.Playing || instance.State == PlaybackState.Sustaining);
            }
            Dom.SetText(row.Position, Dom.Time(instance.TimelinePosition));
        }
    }

    private void OnInstanceClick(NecronomiDom.Dom.Event e)
    {
        if (_ctx is null || Dom.Closest(e, ".istop") is null) return;
        Element? row = Dom.Closest(e, ".inst");
        if (row is null || !int.TryParse(row.GetAttribute("data-index"), out int index) || index < 0 || index >= _shown.Count) return;
        StopInstance(_shown[index]);
    }

    // ---- parameters ------------------------------------------------------------------------------------------------------

    private sealed class SliderRow
    {
        public SliderRow(ParameterDefinition definition, bool global, Element row, Element? slider, Element? fill, Element? value, HTMLElement? dropdown)
        {
            Definition = definition; Global = global; Row = row; Slider = slider; Fill = fill; Value = value; Dropdown = dropdown;
        }
        public ParameterDefinition Definition { get; }
        public bool Global { get; }
        public Element Row { get; }
        public Element? Slider { get; }
        public Element? Fill { get; }
        public Element? Value { get; }
        public HTMLElement? Dropdown { get; }
        public float Shown { get; set; } = float.NaN;
        public bool ReadOnly => Definition.BuiltIn != BuiltInParameter.None;
    }

    private void RebuildParameters()
    {
        if (_ctx is null || _doc is null) return;
        HideDropdown();
        _activeSlider = null;
        _sliders.Clear();
        while (_globalList.FirstChild is { } g) _globalList.RemoveChild(g);
        while (_localList.FirstChild is { } l) _localList.RemoveChild(l);

        List<ParameterDefinition> globals = _ctx.Project.Project.GlobalParameters;
        for (int i = 0; i < globals.Count; i++) AddSlider(_globalList, globals[i], global: true);
        if (globals.Count == 0) Dom.Add(_globalList, "p", "empty", "No global parameters");

        AudioEvent? ev = FindEvent(_selectedEvent);
        Dom.SetText(_localHeading, ev is null ? "Event parameters" : "Event parameters · " + EventTree.NameOf(ev.Path));
        if (ev is null) Dom.Add(_localList, "p", "empty", "Select an event");
        else
        {
            for (int i = 0; i < ev.LocalParameters.Count; i++) AddSlider(_localList, ev.LocalParameters[i], global: false);
            if (ev.LocalParameters.Count == 0) Dom.Add(_localList, "p", "empty", "No local parameters");
        }
        RefreshSliderValues();
    }

    private void AddSlider(Element list, ParameterDefinition definition, bool global)
    {
        HTMLElement row = Dom.Add(list, "div", "param");
        HTMLElement name = Dom.Add(row, "p", "name", definition.Name);
        if (definition.BuiltIn != BuiltInParameter.None) name.ClassList.Add("builtin");
        SliderRow slider;
        if (definition.Kind == ParameterKind.Labeled)
        {
            HTMLElement dropdown = Dom.Add(row, "button", "dd", "—");
            slider = new SliderRow(definition, global, row, null, null, null, dropdown);
        }
        else
        {
            HTMLElement track = Dom.Add(row, "div", definition.BuiltIn != BuiltInParameter.None ? "slider readonly" : "slider");
            HTMLElement fill = Dom.Add(track, "div", "fill");
            fill.Style.SetProperty("transform", Dom.ScaleX(0f)); // present from the first layout: later writes are compositor-only
            HTMLElement value = Dom.Add(row, "p", "pval", "");
            slider = new SliderRow(definition, global, row, track, fill, value, null);
        }
        _sliders.Add(slider);
    }

    private SliderRow? SliderOf(Element? element)
    {
        if (element is null) return null;
        Element? row = element.Closest(".param");
        if (row is null) return null;
        for (int i = 0; i < _sliders.Count; i++) if (ReferenceEquals(_sliders[i].Row, row)) return _sliders[i];
        return null;
    }

    /// <summary>Current value the UI should display: engine state for globals/built-ins, the last set value for locals.</summary>
    private float CurrentValue(SliderRow slider)
    {
        DirgeSystem? system = _ctx!.Audition.System;
        if (slider.Global)
        {
            if (system is not null && system.GlobalParameters.TryGet(slider.Definition.Id, out ParameterState state)) return state.Value;
            return slider.Definition.Default;
        }
        IReadOnlyList<EventInstance> instances = _ctx.Audition.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            if (instances[i].Description.Id == _selectedEvent && instances[i].State != PlaybackState.Stopped) return instances[i].GetParameter(slider.Definition.Name);
        }
        if (_localValues.TryGetValue(_selectedEvent, out Dictionary<string, float>? values) && values.TryGetValue(slider.Definition.Name, out float stored)) return stored;
        return slider.Definition.Default;
    }

    private void RefreshSliderValues()
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            SliderRow slider = _sliders[i];
            if (ReferenceEquals(slider, _activeSlider)) continue;
            ShowValue(slider, CurrentValue(slider));
        }
    }

    private static void ShowValue(SliderRow slider, float value)
    {
        if (slider.Shown == value) return;
        slider.Shown = value;
        ParameterDefinition d = slider.Definition;
        if (slider.Dropdown is not null)
        {
            int index = (int)MathF.Round(value);
            string label = index >= 0 && index < d.Labels.Count ? d.Labels[index] : "—";
            Dom.SetText(slider.Dropdown, label);
            return;
        }
        float range = d.Maximum - d.Minimum;
        float t = range <= 0f ? 0f : Math.Clamp((value - d.Minimum) / range, 0f, 1f);
        ((HTMLElement)slider.Fill!).Style.SetProperty("transform", Dom.ScaleX(t));
        Dom.SetText(slider.Value!, d.Kind == ParameterKind.Discrete ? Dom.F(value, 0) : Dom.F(value));
    }

    private void OnParamPointerDown(NecronomiDom.Dom.Event e)
    {
        if (e is not PointerEvent pointer || _ctx is null) return;
        Element? track = Dom.Closest(e, ".slider");
        SliderRow? slider = SliderOf(track);
        if (slider is null || slider.ReadOnly || track is null) return;
        _activeSlider = slider;
        // Synthetic (script-dispatched) pointer events have no active pointer to capture; real input keeps the drag past the track.
        if (e.IsTrusted) track.SetPointerCapture(pointer.PointerId);
        ApplyPointer(slider, pointer.ClientX);
    }

    private void OnParamPointerMove(NecronomiDom.Dom.Event e)
    {
        if (_activeSlider is null || e is not PointerEvent pointer) return;
        if ((pointer.Buttons & 1) == 0) { _activeSlider = null; return; }
        ApplyPointer(_activeSlider, pointer.ClientX);
    }

    private void OnParamPointerUp(NecronomiDom.Dom.Event e)
    {
        if (_activeSlider is null || e is not PointerEvent pointer) return;
        ApplyPointer(_activeSlider, pointer.ClientX);
        if (e.IsTrusted && _activeSlider.Slider is not null && _activeSlider.Slider.HasPointerCapture(pointer.PointerId)) _activeSlider.Slider.ReleasePointerCapture(pointer.PointerId);
        _activeSlider = null;
    }

    private void ApplyPointer(SliderRow slider, double clientX)
    {
        if (_host is null || slider.Slider is null || !Dom.TryGetBounds(_host, slider.Slider, out Rect bounds) || bounds.Width <= 0) return;
        float t = (float)Math.Clamp((clientX - bounds.X) / bounds.Width, 0.0, 1.0);
        ParameterDefinition d = slider.Definition;
        float value = d.Minimum + t * (d.Maximum - d.Minimum);
        if (d.Kind == ParameterKind.Discrete) value = MathF.Round(value);
        SetParameter(slider, value);
    }

    /// <summary>Applies a parameter value to the audition: globals on the system, locals on every running instance of the event (and remembered for the next Play).</summary>
    private void SetParameter(SliderRow slider, float value)
    {
        if (_ctx is null) return;
        if (slider.Global)
        {
            _ctx.Audition.System?.SetGlobalParameter(slider.Definition.Name, value);
        }
        else
        {
            if (!_localValues.TryGetValue(_selectedEvent, out Dictionary<string, float>? values))
            {
                values = new Dictionary<string, float>();
                _localValues[_selectedEvent] = values;
            }
            values[slider.Definition.Name] = value;
            IReadOnlyList<EventInstance> instances = _ctx.Audition.Instances;
            for (int i = 0; i < instances.Count; i++)
            {
                if (instances[i].Description.Id == _selectedEvent) instances[i].SetParameter(slider.Definition.Name, value);
            }
        }
        ShowValue(slider, value);
    }

    /// <summary>Sets a parameter by name (test and scripting entry point; same semantics as dragging its slider).</summary>
    public bool SetParameter(string name, float value)
    {
        for (int i = 0; i < _sliders.Count; i++)
        {
            if (_sliders[i].Definition.Name == name && !_sliders[i].ReadOnly) { SetParameter(_sliders[i], value); return true; }
        }
        return false;
    }

    // ---- label dropdown ----------------------------------------------------------------------------------------------------

    private void OnParamClick(NecronomiDom.Dom.Event e)
    {
        Element? button = Dom.Closest(e, ".dd");
        SliderRow? slider = SliderOf(button);
        if (slider?.Dropdown is null || _host is null) return;
        if (ReferenceEquals(_dropdownFor, slider)) { HideDropdown(); return; }
        ShowDropdown(slider);
    }

    private void ShowDropdown(SliderRow slider)
    {
        if (_doc is null || _host is null || slider.Dropdown is null) return;
        while (_ddPopup.FirstChild is { } child) _ddPopup.RemoveChild(child);
        int current = (int)MathF.Round(CurrentValue(slider));
        List<string> labels = slider.Definition.Labels;
        for (int i = 0; i < labels.Count; i++)
        {
            HTMLElement item = Dom.Add(_ddPopup, "button", i == current ? "current" : "", labels[i]);
            item.SetAttribute("data-index", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (labels.Count == 0) Dom.Add(_ddPopup, "p", "empty", "No labels");
        _host.FlushPendingLayout();
        if (Dom.TryGetBounds(_host, slider.Dropdown, out Rect bounds))
        {
            _ddPopup.Style.SetProperty("left", Dom.F(bounds.X, 0) + "px");
            _ddPopup.Style.SetProperty("top", Dom.F(bounds.Y + bounds.Height, 0) + "px");
            _ddPopup.Style.SetProperty("min-width", Dom.F(bounds.Width, 0) + "px");
        }
        _ddPopup.Hidden = false;
        _dropdownFor = slider;
    }

    private void HideDropdown()
    {
        _ddPopup.Hidden = true;
        _dropdownFor = null;
    }

    private void OnDropdownItem(NecronomiDom.Dom.Event e)
    {
        SliderRow? slider = _dropdownFor;
        Element? item = Dom.Closest(e, "button");
        if (slider is null || item is null || !int.TryParse(item.GetAttribute("data-index"), out int index)) return;
        SetParameter(slider, index);
        HideDropdown();
    }

    private void OnDocumentPointerDown(NecronomiDom.Dom.Event e)
    {
        if (_dropdownFor is null || e.Target is not Element target) return;
        if (_ddPopup.Contains(target) || target.Closest(".dd") is not null) return;
        HideDropdown();
    }

    // ---- panner ------------------------------------------------------------------------------------------------------------

    /// <summary>The audition applies the preview attributes to its 3D instances and listener 0 (contract); the pane only publishes them.</summary>
    private void OnPannerChanged()
    {
        if (_ctx is null) return;
        _ctx.Audition.PreviewEmitter = _panner.Emitter;
        _ctx.Audition.PreviewListener = _panner.Listener;
    }
}

/// <summary>Root of the transport pane: document on the left, panner canvas on the right; forwards <c>_Process</c> to the pane.</summary>
internal sealed partial class TransportRoot : HSplitContainer
{
    public event Action<double>? Processed;

    public override void _Process(double delta) => Processed?.Invoke(delta);
}
