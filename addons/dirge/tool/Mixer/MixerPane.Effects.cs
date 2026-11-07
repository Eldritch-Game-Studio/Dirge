#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;
using NecronomiDom.Paint;

namespace Dirge.Tool.Mixer;

public sealed partial class MixerPane
{
    /// <summary>One row of a strip's effect chain: an authored effect, or the bus fader marker (<see cref="IsFader"/>).</summary>
    private sealed class EffectRow
    {
        public const string FaderTitle = "Bus fader. Effects above are pre-fader, effects below post-fader (the engine inserts an Amplify fader here). Drag to move.";

        public HTMLElement Root { get; }
        public HTMLElement? Enable { get; }
        public HTMLElement Name { get; }
        public HTMLElement? Remove { get; }
        /// <summary>Authored effect index; -1 for the fader row.</summary>
        public int Index { get; }
        /// <summary>Display position in the chain list (effects and the fader share one sequence).</summary>
        public int Slot { get; }
        public DirgeId EffectId { get; }
        public bool IsFader => Index < 0;

        public EffectRow(Dom dom, HTMLElement list, BusEffect effect, int index, int slot, bool selected, bool scoped)
        {
            Index = index; Slot = slot; EffectId = effect.Id;
            Root = dom.El(list, "div", "fx-item");
            Root.Dataset["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (selected) Root.ClassList.Add("selected");
            if (scoped) Root.ClassList.Add("scoped");
            Enable = dom.El(Root, "button", effect.Enabled ? "fx-en on" : "fx-en", "");
            Enable.Title = effect.Enabled ? "Disable effect" : "Enable effect";
            Name = dom.El(Root, "div", effect.Enabled ? "fx-name" : "fx-name disabled", EffectCatalog.DisplayName(effect));
            Name.Title = effect.TypeName + " — click for parameters, drag to reorder";
            Remove = dom.El(Root, "button", "fx-x", "x");
            Remove.Title = "Remove effect";
        }

        /// <summary>Fader marker row.</summary>
        public EffectRow(Dom dom, HTMLElement list, int slot)
        {
            Index = -1; Slot = slot; EffectId = DirgeId.None;
            Root = dom.El(list, "div", "fx-item fx-fader");
            Root.Dataset["slot"] = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Name = dom.El(Root, "div", "fx-name fx-fader-name", "FADER");
            Name.Title = FaderTitle;
        }
    }

    /// <summary>Row of the parameter popover; kept so refreshes can update values in place.</summary>
    private sealed class ParameterRow
    {
        public EffectParameter Parameter { get; }
        public HTMLElement Label { get; }
        public Slider? Slider { get; }
        public HTMLInputElement? Input { get; }
        public HTMLElement? Toggle { get; }
        public HTMLElement? EnumButton { get; }
        public HTMLElement Unscope { get; }
        public object Token { get; } = new();

        public ParameterRow(EffectParameter parameter, HTMLElement label, Slider? slider, HTMLInputElement? input, HTMLElement? toggle, HTMLElement? enumButton, HTMLElement unscope)
        {
            Parameter = parameter; Label = label; Slider = slider; Input = input; Toggle = toggle; EnumButton = enumButton; Unscope = unscope;
        }
    }

    private HTMLElement _popover = null!;
    private HTMLElement _popoverTitle = null!;
    private HTMLElement _popoverBody = null!;
    private DirgeId _popoverBus;
    private DirgeId _popoverEffect;
    private Dom? _popoverDom;
    private readonly List<ParameterRow> _parameterRows = new();
    private readonly List<string> _effectTypes = new();

    // Effect row drag state.
    private BusStrip? _dragFxStrip;
    private EffectRow? _dragFxRow;
    private int _dragFxPointer;
    private double _dragFxStartY;
    private bool _dragFxActive;
    private int _dragFxTargetIndex = -1;
    private bool _suppressFxClick;

    private void BindEffects()
    {
        Dom dom = _dom!;
        _popover = dom.Required("popover");
        _popoverTitle = dom.Required("popover-title");
        _popoverBody = dom.Required("popover-body");
        dom.On(dom.Required("popover-close"), "click", ClosePopover);
        dom.On(dom.Document, "pointerdown", OnDocumentPointerDownForPopover);
    }

    private void UnbindEffects()
    {
        ClosePopover();
    }

    /// <summary>Display slot of the bus fader in the chain list: 0..Effects.Count (Count = after every effect, the default).</summary>
    private static int FaderSlot(Bus bus) => Math.Clamp(bus.FaderIndex, 0, bus.Effects.Count);

    private void RebuildEffectRows(BusStrip strip, Bus bus, Snapshot? editing, int selectedIndex)
    {
        Dom dom = _dom!;
        strip.FxList.TextContent = "";
        strip.Effects.Clear();
        int faderSlot = FaderSlot(bus);
        for (int slot = 0; slot <= bus.Effects.Count; slot++)
        {
            if (slot == faderSlot)
            {
                var fader = new EffectRow(dom, strip.FxList, slot);
                strip.Effects.Add(fader);
                BindChainDrag(strip, fader);
                continue;
            }

            int i = slot < faderSlot ? slot : slot - 1;
            BusEffect effect = bus.Effects[i];
            bool scoped = editing is not null && HasScopedParameter(editing, bus.Id, i);
            var row = new EffectRow(dom, strip.FxList, effect, i, slot, i == selectedIndex, scoped);
            strip.Effects.Add(row);
            int index = i;
            dom.On(row.Enable!, "click", e => { e.StopPropagation(); ToggleEffectEnabled(strip.Id, index); });
            dom.On(row.Remove!, "click", e => { e.StopPropagation(); RemoveEffect(strip.Id, index); });
            BindChainDrag(strip, row);
            dom.On(row.Name, "click", e =>
            {
                if (_suppressFxClick) { _suppressFxClick = false; return; }
                Selection.Select(SelectionKind.Effect, effect.Id, strip.Id, index);
                OpenPopover(strip.Id, index, e);
            });
        }
    }

    private void BindChainDrag(BusStrip strip, EffectRow row)
    {
        Dom dom = _dom!;
        dom.On(row.Name, "pointerdown", e => OnEffectPointerDown(strip, row, e));
        dom.On(row.Name, "pointermove", e => OnEffectPointerMove(strip, row, e));
        dom.On(row.Name, "pointerup", e => OnEffectPointerUp(strip, row, e));
        dom.On(row.Name, "pointercancel", e => OnEffectPointerUp(strip, row, e));
    }

    private static bool HasScopedParameter(Snapshot snapshot, DirgeId bus, int effectIndex)
    {
        for (int i = 0; i < snapshot.Values.Count; i++)
        {
            PropertyRef target = snapshot.Values[i].Target;
            if (target.Kind == PropertyKind.EffectParameter && target.Owner == bus && target.EffectIndex == effectIndex) return true;
        }
        return false;
    }

    // ---- chain edits -----------------------------------------------------------------------------------------------------

    private void ToggleEffectEnabled(DirgeId busId, int index)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, busId);
        if (bus is null || index < 0 || index >= bus.Effects.Count) return;
        BusEffect effect = bus.Effects[index];
        bool before = effect.Enabled;
        Project.Mutate(before ? "Disable effect" : "Enable effect", ChangeScope.Mixer, () => effect.Enabled = !before, () => effect.Enabled = before, busId);
    }

    private void RemoveEffect(DirgeId busId, int index)
    {
        AudioProject project = Model;
        MixerGraphOps.EffectRemoval? plan = MixerGraphOps.PlanRemoveEffect(project, busId, index);
        if (plan is null) return;
        if (_popoverBus == busId && _popoverEffect == plan.Effect.Id) ClosePopover();
        if (Selection.Kind == SelectionKind.Effect && Selection.Id == plan.Effect.Id) Selection.Select(SelectionKind.Bus, busId);
        Project.Mutate("Remove effect", ChangeScope.Mixer | (plan.RemovedValues.Count > 0 ? ChangeScope.Snapshots : ChangeScope.None),
            () => MixerGraphOps.ApplyRemoveEffect(project, plan),
            () => MixerGraphOps.RevertRemoveEffect(project, plan), busId);
    }

    private void AddEffect(DirgeId busId, string typeName) => AddEffect(busId, new BusEffect { TypeName = typeName }, "Add effect");

    private void AddEffect(DirgeId busId, BusEffect effect, string label)
    {
        AudioProject project = Model;
        Bus? bus = MixerGraphOps.FindBus(project.Mixer, busId);
        if (bus is null) return;
        int index = bus.Effects.Count;
        Project.Mutate(label, ChangeScope.Mixer,
            () => { if (!bus.Effects.Contains(effect)) MixerGraphOps.InsertEffect(project, busId, effect, index); },
            () => MixerGraphOps.UninsertEffect(project, busId, effect), busId);
        Selection.Select(SelectionKind.Effect, effect.Id, busId, index);
    }

    /// <summary>
    /// Drop of chain row <paramref name="row"/> at display slot <paramref name="targetSlot"/> (position after the row is taken out).
    /// An effect crossing the fader changes <see cref="Bus.FaderIndex"/> along with its order; dragging the fader row only moves the fader.
    /// </summary>
    private void MoveChainRow(DirgeId busId, EffectRow row, int targetSlot)
    {
        AudioProject project = Model;
        Bus? bus = MixerGraphOps.FindBus(project.Mixer, busId);
        if (bus is null) return;
        int count = bus.Effects.Count;
        if (targetSlot < 0 || targetSlot > count || row.Slot < 0 || row.Slot > count || targetSlot == row.Slot) return;
        int faderSlot = FaderSlot(bus);

        // Chain as displayed: authored effect indices with -1 for the fader; then the list move.
        var display = new List<int>(count + 1);
        for (int slot = 0; slot <= count; slot++) display.Add(slot == faderSlot ? -1 : slot < faderSlot ? slot : slot - 1);
        int moved = display[row.Slot];
        display.RemoveAt(row.Slot);
        display.Insert(targetSlot, moved);

        int newFaderSlot = display.IndexOf(-1);
        int from = moved, to = -1;
        if (moved >= 0)
        {
            to = 0;
            for (int s = 0; s < display.Count && display[s] != moved; s++) if (display[s] >= 0) to++;
        }
        bool reorder = moved >= 0 && from != to;
        bool faderMoved = newFaderSlot != faderSlot;
        if (!reorder && !faderMoved) return;
        int oldFader = bus.FaderIndex;
        int newFader = newFaderSlot >= count ? int.MaxValue : newFaderSlot;

        Project.Mutate(moved >= 0 ? "Reorder effects" : "Move fader", ChangeScope.Mixer | (reorder ? ChangeScope.Snapshots : ChangeScope.None),
            () => { if (reorder) MixerGraphOps.MoveEffect(project, busId, from, to); if (faderMoved) bus.FaderIndex = newFader; },
            () => { if (faderMoved) bus.FaderIndex = oldFader; if (reorder) MixerGraphOps.MoveEffect(project, busId, to, from); }, busId);
        if (!reorder) return;
        if (Selection.Kind == SelectionKind.Effect && Selection.Id == row.EffectId) Selection.Select(SelectionKind.Effect, row.EffectId, busId, to);
        if (_popoverBus == busId && _popoverEffect == row.EffectId) RefreshPopover(EditingSnapshot());
    }

    private void ShowAddEffectPopup(BusStrip strip, Event e)
    {
        _effectTypes.Clear();
        _effectTypes.AddRange(EffectCatalog.AvailableTypes());
        _popupItems.Clear();
        (double x, double y) = PopupAnchor(strip.FxAdd, e);
        _popupItems.Add(new Popup.Item("Ducking preset…", () => ShowDuckingSourcePicker(strip, x, y)));
        for (int i = 0; i < _effectTypes.Count; i++)
        {
            string type = _effectTypes[i];
            _popupItems.Add(new Popup.Item(EffectCatalog.DisplayName(type), () => AddEffect(strip.Id, type)));
        }
        _popup.Show("ADD EFFECT", _popupItems, x, y);
    }

    /// <summary>Second step of the ducking preset: pick the bus whose level keys the compressor (any bus but this one).</summary>
    private void ShowDuckingSourcePicker(BusStrip strip, double x, double y)
    {
        MixerGraph mixer = Model.Mixer;
        _popupItems.Clear();
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            Bus source = mixer.Buses[i];
            if (source.Id == strip.Id) continue;
            string name = source.Name;
            _popupItems.Add(new Popup.Item(name, () => AddEffect(strip.Id, EffectCatalog.DuckingPreset(name), "Add ducking")));
        }
        _popup.Show("DUCK FROM", _popupItems, x, y);
    }

    // ---- drag reorder ----------------------------------------------------------------------------------------------------

    private void OnEffectPointerDown(BusStrip strip, EffectRow row, Event e)
    {
        if (e is not PointerEvent p || p.Button != 0) return;
        e.StopPropagation();
        if (Selection.Kind != SelectionKind.Bus || Selection.Id != strip.Id) Selection.Select(SelectionKind.Bus, strip.Id);
        _dragFxStrip = strip; _dragFxRow = row; _dragFxPointer = p.PointerId; _dragFxStartY = p.ClientY; _dragFxActive = false; _dragFxTargetIndex = -1;
        row.Name.SetPointerCapture(p.PointerId);
    }

    private void OnEffectPointerMove(BusStrip strip, EffectRow row, Event e)
    {
        if (!ReferenceEquals(_dragFxRow, row) || e is not PointerEvent p) return;
        if (!_dragFxActive)
        {
            if (Math.Abs(p.ClientY - _dragFxStartY) < 5) return;
            _dragFxActive = true;
            row.Root.ClassList.Add("dragging");
            _popup.Hide();
        }
        ClearEffectDropMarks(strip);
        _dragFxTargetIndex = -1;
        HTMLElement? under = Dom.Closest(_dom!.HitTest(p.ClientX, p.ClientY), "fx-item");
        if (under is null || !ReferenceEquals(under.ParentNode, strip.FxList)) return;
        string? text = under.Dataset["slot"];
        if (text is null || !int.TryParse(text, out int hoverSlot) || hoverSlot == row.Slot) return;
        bool after = _dom.TryGetBounds(under, out Rect bounds) && p.ClientY > bounds.Y + bounds.Height / 2;
        int target = after ? hoverSlot + 1 : hoverSlot;
        if (target > row.Slot) target--;
        if (target == row.Slot) return;
        _dragFxTargetIndex = target;
        Dom.SetClass(under, after ? "drop-after" : "drop-before", true);
    }

    private void OnEffectPointerUp(BusStrip strip, EffectRow row, Event e)
    {
        if (!ReferenceEquals(_dragFxRow, row)) return;
        if (row.Name.HasPointerCapture(_dragFxPointer)) row.Name.ReleasePointerCapture(_dragFxPointer);
        ClearEffectDropMarks(strip);
        row.Root.ClassList.Remove("dragging");
        bool active = _dragFxActive;
        int target = _dragFxTargetIndex;
        _dragFxRow = null; _dragFxStrip = null; _dragFxActive = false; _dragFxTargetIndex = -1;
        if (!active) return;
        _suppressFxClick = !row.IsFader; // the fader row has no click handler to consume the flag
        if (target >= 0) MoveChainRow(strip.Id, row, target);
    }

    private static void ClearEffectDropMarks(BusStrip strip)
    {
        for (int i = 0; i < strip.Effects.Count; i++)
        {
            Dom.SetClass(strip.Effects[i].Root, "drop-before", false);
            Dom.SetClass(strip.Effects[i].Root, "drop-after", false);
        }
    }

    // ---- parameter popover -------------------------------------------------------------------------------------------------

    private void OpenPopover(DirgeId busId, int index, Event e)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, busId);
        if (bus is null || index < 0 || index >= bus.Effects.Count) return;
        _popup.Hide();
        _popoverBus = busId;
        _popoverEffect = bus.Effects[index].Id;
        BuildPopover(bus, index, EditingSnapshot());
        double x = 0, y = 0;
        if (e is MouseEvent m) { x = m.ClientX + 8; y = m.ClientY - 10; }
        Godot.Vector2 size = _dom!.Host.Size;
        _popover.Style.SetProperty("left", Dom.Px((int)Math.Clamp(x, 0, Math.Max(0, size.X - 322))));
        _popover.Style.SetProperty("top", Dom.Px((int)Math.Clamp(y, 0, Math.Max(0, size.Y - 200))));
        _popover.Hidden = false;
    }

    private void ClosePopover()
    {
        if (_popover is null) return;
        _popover.Hidden = true;
        _popoverDom?.DetachAll();
        _popoverDom = null;
        _parameterRows.Clear();
        _popoverBody.TextContent = "";
        _popoverBus = DirgeId.None;
        _popoverEffect = DirgeId.None;
    }

    private int PopoverEffectIndex(out Bus? bus)
    {
        bus = _popoverBus.IsNone ? null : MixerGraphOps.FindBus(Model.Mixer, _popoverBus);
        if (bus is null) return -1;
        for (int i = 0; i < bus.Effects.Count; i++)
        {
            if (bus.Effects[i].Id == _popoverEffect) return i;
        }
        return -1;
    }

    /// <summary>After a project change: update values in place, or close when the effect is gone.</summary>
    private void RefreshPopover(Snapshot? editing)
    {
        if (_popover is null || _popover.Hidden) return;
        int index = PopoverEffectIndex(out Bus? bus);
        if (bus is null || index < 0) { ClosePopover(); return; }
        BusEffect effect = bus.Effects[index];
        for (int i = 0; i < _parameterRows.Count; i++) ApplyParameterRow(_parameterRows[i], bus, index, effect, editing);
    }

    private void BuildPopover(Bus bus, int index, Snapshot? editing)
    {
        _popoverDom?.DetachAll();
        _parameterRows.Clear();
        _popoverBody.TextContent = "";
        var dom = new Dom(_dom!.Host);
        _popoverDom = dom;
        BusEffect effect = bus.Effects[index];
        Dom.SetText(_popoverTitle, bus.Name + " / " + EffectCatalog.DisplayName(effect) + (editing is not null ? "  [snapshot " + editing.Name + "]" : ""));

        EffectParameter[] parameters = EffectCatalog.Parameters(effect.TypeName);
        if (parameters.Length == 0)
        {
            dom.El(_popoverBody, "div", "param-empty", Godot.ClassDB.ClassExists(effect.TypeName) ? "This effect has no editable parameters." : "Class '" + effect.TypeName + "' is not available in this engine build.");
            return;
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            EffectParameter parameter = parameters[i];
            HTMLElement row = dom.El(_popoverBody, "div", "param-row");
            HTMLElement label = dom.El(row, "div", "field-label", parameter.Label);
            label.Title = parameter.Name;
            Slider? slider = null;
            HTMLInputElement? input = null;
            HTMLElement? toggle = null;
            HTMLElement? enumButton = null;
            switch (parameter.Kind)
            {
                case EffectParameterKind.Float:
                case EffectParameterKind.Int:
                {
                    HTMLElement track = dom.El(row, "div", "hslider");
                    HTMLElement fill = dom.El(track, "div", "hfill");
                    HTMLElement thumb = dom.El(track, "div", "hthumb");
                    slider = new Slider(dom, track, thumb, fill, vertical: false, parameter.Min, parameter.Max, HSliderLengthPx, HSliderThumbPx)
                    {
                        Step = parameter.Kind == EffectParameterKind.Int ? 1f : parameter.Step,
                        WheelStep = parameter.Kind == EffectParameterKind.Int ? 1f : parameter.Step,
                        ResetValue = parameter.DefaultValue,
                    };
                    input = dom.Input(row, "num", "");
                    break;
                }
                case EffectParameterKind.Bool:
                    toggle = dom.El(row, "button", "pill", "Off");
                    break;
                case EffectParameterKind.Enum:
                    enumButton = dom.El(row, "button", "pill", "");
                    break;
                case EffectParameterKind.String:
                    input = dom.Input(row, "num text", "");
                    break;
            }
            HTMLElement unscope = dom.El(row, "button", "unscope-inline", "x");
            unscope.Title = "Unscope from snapshot";
            unscope.Hidden = true;
            var view = new ParameterRow(parameter, label, slider, input, toggle, enumButton, unscope);
            _parameterRows.Add(view);
            WireParameterRow(dom, view);
            ApplyParameterRow(view, bus, index, effect, editing);
        }
    }

    private void WireParameterRow(Dom dom, ParameterRow view)
    {
        EffectParameter parameter = view.Parameter;
        if (view.Slider is not null)
        {
            view.Slider.ValueChanged += (value, continuous) => WriteEffectParameter(view, value, continuous);
            view.Slider.DragEnded += EndContinuous;
        }
        if (view.Input is not null)
        {
            void Commit()
            {
                if (parameter.Kind == EffectParameterKind.String) { WriteEffectString(view, view.Input!.Value); return; }
                if (Dom.TryParse(view.Input!.Value, out float parsed)) WriteEffectParameter(view, Math.Clamp(parsed, parameter.Min, parameter.Max), false);
                else RefreshPopover(EditingSnapshot());
            }
            dom.On(view.Input, "change", Commit);
            dom.On(view.Input, "keydown", e => { if (e is KeyboardEvent k && k.Key == "Enter") { e.PreventDefault(); Commit(); } });
        }
        if (view.Toggle is not null)
        {
            dom.On(view.Toggle, "click", () =>
            {
                float current = CurrentParameterValue(view, out _);
                WriteEffectParameter(view, current >= 0.5f ? 0f : 1f, false);
            });
        }
        if (view.EnumButton is not null)
        {
            dom.On(view.EnumButton, "click", e =>
            {
                float current = CurrentParameterValue(view, out _);
                _popupItems.Clear();
                for (int i = 0; i < parameter.EnumLabels.Length; i++)
                {
                    int option = i;
                    _popupItems.Add(new Popup.Item(parameter.EnumLabels[i], () => WriteEffectParameter(view, option, false), current: (int)MathF.Round(current) == i));
                }
                (double x, double y) = PopupAnchor(view.EnumButton, e);
                _popup.Show(parameter.Label.ToUpperInvariant(), _popupItems, x, y);
            });
        }
        dom.On(view.Unscope, "click", () =>
        {
            Snapshot? editing = EditingSnapshot();
            int index = PopoverEffectIndex(out Bus? bus);
            if (editing is null || bus is null || index < 0) return;
            UnscopeProperty(editing, PropertyRef.Effect(bus.Id, index, parameter.Name));
        });
    }

    /// <summary>Value the row should display: snapshot value when scoped, else authored, else the engine default.</summary>
    private float CurrentParameterValue(ParameterRow view, out bool scoped)
    {
        scoped = false;
        int index = PopoverEffectIndex(out Bus? bus);
        if (bus is null || index < 0) return view.Parameter.DefaultValue;
        BusEffect effect = bus.Effects[index];
        Snapshot? editing = EditingSnapshot();
        if (editing is not null && MixerGraphOps.FindValue(editing, PropertyRef.Effect(bus.Id, index, view.Parameter.Name)) is { } value)
        {
            scoped = true;
            return value.Value;
        }
        return effect.Parameters.TryGetValue(view.Parameter.Name, out float authored) ? authored : view.Parameter.DefaultValue;
    }

    private void ApplyParameterRow(ParameterRow view, Bus bus, int index, BusEffect effect, Snapshot? editing)
    {
        EffectParameter parameter = view.Parameter;
        bool scoped = false;
        float value = effect.Parameters.TryGetValue(parameter.Name, out float authored) ? authored : parameter.DefaultValue;
        if (editing is not null && MixerGraphOps.FindValue(editing, PropertyRef.Effect(bus.Id, index, parameter.Name)) is { } scopedValue)
        {
            scoped = true;
            value = scopedValue.Value;
        }
        Dom.SetClass(view.Label, "scoped", scoped);
        view.Unscope.Hidden = !scoped;
        bool focused = view.Input is not null && ReferenceEquals(view.Input.OwnerDocument?.FocusedElement, view.Input);
        switch (parameter.Kind)
        {
            case EffectParameterKind.Float:
            case EffectParameterKind.Int:
                if (view.Slider is not null)
                {
                    if (!view.Slider.IsDragging) view.Slider.SetValue(value);
                    Dom.SetClass(view.Slider.Track, "scoped", scoped);
                }
                if (view.Input is not null && !focused)
                {
                    string text = Dom.Num(value, parameter.Kind == EffectParameterKind.Int ? 1f : parameter.Step);
                    if (view.Input.Value != text) view.Input.Value = text;
                }
                break;
            case EffectParameterKind.Bool:
                if (view.Toggle is not null)
                {
                    Dom.SetText(view.Toggle, value >= 0.5f ? "On" : "Off");
                    Dom.SetClass(view.Toggle, "on", value >= 0.5f);
                }
                break;
            case EffectParameterKind.Enum:
                if (view.EnumButton is not null)
                {
                    int option = (int)MathF.Round(value);
                    string label = option >= 0 && option < parameter.EnumLabels.Length ? parameter.EnumLabels[option] : option.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Dom.SetText(view.EnumButton, label + " v");
                }
                break;
            case EffectParameterKind.String:
                if (view.Input is not null && !focused)
                {
                    string text = effect.StringParameters.TryGetValue(parameter.Name, out string? s) ? s : parameter.DefaultString;
                    if (view.Input.Value != text) view.Input.Value = text;
                }
                break;
        }
    }

    private void WriteEffectParameter(ParameterRow view, float value, bool continuous)
    {
        int index = PopoverEffectIndex(out Bus? bus);
        if (bus is null || index < 0) return;
        BusEffect effect = bus.Effects[index];
        string name = view.Parameter.Name;
        bool had = effect.Parameters.TryGetValue(name, out float before);
        WriteProperty(view.Token, "Effect " + view.Parameter.Label, PropertyRef.Effect(bus.Id, index, name), value, continuous,
            () => effect.Parameters[name] = value,
            () => { if (had) effect.Parameters[name] = before; else effect.Parameters.Remove(name); },
            bus.Id);
        if (!continuous) EndContinuous();
    }

    private void WriteEffectString(ParameterRow view, string value)
    {
        int index = PopoverEffectIndex(out Bus? bus);
        if (bus is null || index < 0) return;
        BusEffect effect = bus.Effects[index];
        string name = view.Parameter.Name;
        bool had = effect.StringParameters.TryGetValue(name, out string? before);
        if (had && before == value) return;
        Project.Mutate("Effect " + view.Parameter.Label, ChangeScope.Mixer,
            () => effect.StringParameters[name] = value,
            () => { if (had) effect.StringParameters[name] = before!; else effect.StringParameters.Remove(name); }, bus.Id);
    }

    private void OnDocumentPointerDownForPopover(Event e)
    {
        if (_popover is null || _popover.Hidden) return;
        for (NecronomiDom.Dom.Node? node = e.Target as NecronomiDom.Dom.Node; node is not null; node = node.ParentNode)
        {
            if (ReferenceEquals(node, _popover) || ReferenceEquals(node, _popup.Root)) return;
            // Clicking another effect name re-targets the popover instead of closing it.
            if (node is HTMLElement html && html.ClassList.Contains("fx-name")) return;
        }
        ClosePopover();
    }
}
