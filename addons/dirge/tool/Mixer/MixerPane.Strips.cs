#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Paint;

namespace Dirge.Tool.Mixer;

public sealed partial class MixerPane
{
    /// <summary>DOM view of one bus. Created once per bus id; <see cref="Refresh"/> updates only what changed.</summary>
    private sealed class BusStrip
    {
        private const double HoldSeconds = 1.5;
        private const float HoldDecayDbPerSecond = 24f;
        private const float MeterFloorDb = -60f;
        private const float MeterCeilDb = 6f;

        public DirgeId Id { get; }
        public bool IsMaster { get; }
        public HTMLElement Root { get; }
        public HTMLElement Name { get; }
        public HTMLInputElement NameEdit { get; }
        public HTMLElement MuteButton { get; }
        public HTMLElement SoloButton { get; }
        public HTMLElement BypassButton { get; }
        public HTMLElement FaderTrack { get; }
        public HTMLElement Unscope { get; }
        public Slider Fader { get; }
        public HTMLElement DbText { get; }
        public HTMLElement PeakText { get; }
        public HTMLElement Route { get; }
        public HTMLElement FxList { get; }
        public HTMLElement FxAdd { get; }
        /// <summary>Effect-section footer: opens the per-bus LFO (tremolo) popover; <c>on</c> while the bus volume has an LFO.</summary>
        public HTMLElement LfoButton { get; }
        public HTMLInputElement MaxVoices { get; }
        public List<EffectRow> Effects { get; } = new();
        public object FaderToken { get; } = new();

        private readonly HTMLElement _barL, _barR, _hotL, _hotR, _hold, _clip;
        private int _lastL = -1, _lastR = -1, _lastHotL = -1, _lastHotR = -1, _lastHold = -1;
        private bool _clipOn;
        private float _holdDb = -80f;
        private double _holdAge;
        private string _dbTextValue = "";

        public BusStrip(Dom dom, DirgeId id, bool isMaster)
        {
            Id = id; IsMaster = isMaster;
            Root = dom.El("div", isMaster ? "strip master" : "strip");
            Root.Dataset["bus"] = id.ToString();

            HTMLElement head = dom.El(Root, "div", "strip-head");
            Name = dom.El(head, "div", "strip-name", "");
            Name.Title = isMaster ? "Master bus" : "Double-click to rename, drag to reorder";
            NameEdit = dom.Input(head, "name-edit", "");
            NameEdit.Hidden = true;
            if (!isMaster)
            {
                HTMLElement remove = dom.El(head, "button", "strip-x", "x");
                remove.Title = "Delete bus (children re-route to its parent)";
                DeleteButton = remove;
            }

            HTMLElement toggles = dom.El(Root, "div", "toggles");
            MuteButton = dom.El(toggles, "button", "tog mute", "M");
            SoloButton = dom.El(toggles, "button", "tog solo", "S");
            BypassButton = dom.El(toggles, "button", "tog bypass", "B");
            MuteButton.Title = "Mute"; SoloButton.Title = "Solo"; BypassButton.Title = "Bypass effects";

            HTMLElement faderRow = dom.El(Root, "div", "fader-row");
            FaderTrack = dom.El(faderRow, "div", "fader");
            dom.El(FaderTrack, "div", "rail");
            HTMLElement zero = dom.El(FaderTrack, "div", "zero");
            zero.Style.SetProperty("top", Dom.Px(ZeroLinePx()));
            HTMLElement thumb = dom.El(FaderTrack, "div", "thumb");
            Unscope = dom.El(FaderTrack, "button", "unscope", "x");
            Unscope.Title = "Unscope volume from this snapshot";
            Unscope.Hidden = true;
            Fader = new Slider(dom, FaderTrack, thumb, null, vertical: true, FaderMinDb, FaderMaxDb, FaderLengthPx - 2, FaderThumbPx) { WheelStep = 0.5f, ResetValue = 0f };

            HTMLElement meter = dom.El(faderRow, "div", "meter");
            _barL = dom.El(meter, "div", "bar l");
            _barR = dom.El(meter, "div", "bar r");
            _hotL = dom.El(meter, "div", "bar l hot");
            _hotR = dom.El(meter, "div", "bar r hot");
            _hold = dom.El(meter, "div", "hold");
            _clip = dom.El(meter, "div", "clip");
            // Inline transforms exist from the first layout so every later write is a compositor-only update.
            _barL.Style.SetProperty("transform", Dom.ScaleY(0, MeterInnerPx));
            _barR.Style.SetProperty("transform", Dom.ScaleY(0, MeterInnerPx));
            _hotL.Style.SetProperty("transform", Dom.ScaleY(0, MeterHotPx));
            _hotR.Style.SetProperty("transform", Dom.ScaleY(0, MeterHotPx));
            _hold.Style.SetProperty("transform", Dom.TranslateYUp(0));
            _clip.Title = "Clip indicator (click to reset)";
            dom.On(_clip, "click", ResetClip);

            DbText = dom.El(Root, "div", "db", "0.0 dB");
            PeakText = dom.El(Root, "div", "peak", "");
            PeakText.Hidden = !isMaster;

            Route = dom.El(Root, "button", "route", "");
            Route.Title = "Output bus";
            Route.Hidden = isMaster;

            HTMLElement fxHead = dom.El(Root, "div", "fx-head");
            dom.El(fxHead, "div", "fx-label", "EFFECTS");
            FxAdd = dom.El(fxHead, "button", "fx-add", "+");
            FxAdd.Title = "Add effect";
            FxList = dom.El(Root, "div", "fx-list");
            HTMLElement fxFoot = dom.El(Root, "div", "fx-foot");
            LfoButton = dom.El(fxFoot, "button", "fx-lfo", "LFO");
            LfoButton.Title = "LFO modulators on this bus volume (tremolo)";

            HTMLElement voices = dom.El(Root, "div", "voices-row");
            dom.El(voices, "div", "field-label", "Max voices");
            MaxVoices = dom.Input(voices, "num", "0");
            MaxVoices.Title = "Hard cap on simultaneous real voices routed here (0 = unlimited)";
        }

        public HTMLElement? DeleteButton { get; }

        private static int ZeroLinePx()
        {
            float norm = (0f - FaderMinDb) / (FaderMaxDb - FaderMinDb);
            return (int)MathF.Round((1f - norm) * (FaderLengthPx - 2 - FaderThumbPx)) + FaderThumbPx / 2;
        }

        public void Refresh(Bus bus, Bus? parent, Snapshot? editing, bool selected)
        {
            Dom.SetText(Name, bus.Name);
            Dom.SetClass(Root, "selected", selected);
            Dom.SetClass(Root, "snap-mode", editing is not null);
            Dom.SetClass(MuteButton, "on", bus.Mute);
            Dom.SetClass(SoloButton, "on", bus.Solo);
            Dom.SetClass(BypassButton, "on", bus.BypassEffects);

            PropertyRef volume = PropertyRef.BusVolume(bus.Id);
            SnapshotValue? scoped = editing is not null ? MixerGraphOps.FindValue(editing, volume) : null;
            float shown = scoped?.Value ?? bus.VolumeDb;
            if (!Fader.IsDragging) Fader.SetValue(shown);
            Dom.SetClass(FaderTrack, "scoped", scoped is not null);
            Dom.SetClass(FaderTrack, "unscoped-ref", editing is not null && scoped is null);
            Dom.SetClass(DbText, "scoped", scoped is not null);
            Unscope.Hidden = scoped is null;
            string db = Dom.Db(shown);
            if (db != _dbTextValue) { _dbTextValue = db; DbText.TextContent = db; }

            if (!IsMaster) Dom.SetText(Route, "> " + (parent?.Name ?? "(none)"));
            if (!ReferenceEquals(MaxVoices.OwnerDocument?.FocusedElement, MaxVoices))
            {
                string voices = bus.MaxVoices.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (MaxVoices.Value != voices) MaxVoices.Value = voices;
            }
        }
        /// <summary>Only structural changes replace rows; selection, enabled and scoped states update in place.</summary>
        public bool EffectChainChanged(Bus bus)
        {
            if (Effects.Count != bus.Effects.Count + 1) return true;
            int faderSlot = FaderSlot(bus);
            for (int slot = 0; slot < Effects.Count; slot++)
            {
                EffectRow row = Effects[slot];
                if (slot == faderSlot)
                {
                    if (!row.IsFader) return true;
                    continue;
                }
                int index = slot < faderSlot ? slot : slot - 1;
                if (row.IsFader || row.EffectId != bus.Effects[index].Id || row.TypeName != bus.Effects[index].TypeName) return true;
            }
            return false;
        }

        public void UpdateMeter(float leftDb, float rightDb, double delta)
        {
            int l = MeterPx(leftDb), r = MeterPx(rightDb);
            if (l != _lastL) { _lastL = l; _barL.Style.SetProperty("transform", Dom.ScaleY(Math.Min(l, MeterHotFromPx), MeterInnerPx)); }
            if (r != _lastR) { _lastR = r; _barR.Style.SetProperty("transform", Dom.ScaleY(Math.Min(r, MeterHotFromPx), MeterInnerPx)); }
            int hotL = Math.Max(0, l - MeterHotFromPx), hotR = Math.Max(0, r - MeterHotFromPx);
            if (hotL != _lastHotL) { _lastHotL = hotL; _hotL.Style.SetProperty("transform", Dom.ScaleY(hotL, MeterHotPx)); }
            if (hotR != _lastHotR) { _lastHotR = hotR; _hotR.Style.SetProperty("transform", Dom.ScaleY(hotR, MeterHotPx)); }

            float peak = MathF.Max(leftDb, rightDb);
            if (peak >= _holdDb) { _holdDb = peak; _holdAge = 0; }
            else
            {
                _holdAge += delta;
                if (_holdAge > HoldSeconds) _holdDb = MathF.Max(peak, _holdDb - HoldDecayDbPerSecond * (float)delta);
            }
            int hold = MeterPx(_holdDb);
            if (hold != _lastHold) { _lastHold = hold; _hold.Style.SetProperty("transform", Dom.TranslateYUp(hold)); }
            if (peak > 0f && !_clipOn) { _clipOn = true; _clip.ClassList.Add("on"); }
        }

        public float HoldDb => _holdDb;

        private void ResetClip()
        {
            if (!_clipOn) return;
            _clipOn = false;
            _clip.ClassList.Remove("on");
        }

        private static int MeterPx(float db)
        {
            if (db <= MeterFloorDb) return 0;
            float norm = (MathF.Min(db, MeterCeilDb) - MeterFloorDb) / (MeterCeilDb - MeterFloorDb);
            return (int)MathF.Round(norm * MeterInnerPx);
        }

        /// <summary>Bar pixels below the −6 dB "hot" threshold; the yellow segment covers the remaining <see cref="MeterHotPx"/> (mixer.css).</summary>
        private const int MeterHotFromPx = 121;
        private const int MeterHotPx = MeterInnerPx - MeterHotFromPx;

        public void Dispose() => Root.Remove();
    }

    private readonly Dictionary<DirgeId, BusStrip> _strips = new();
    private readonly List<DirgeId> _stripOrder = new();
    private readonly List<Bus> _routingTargets = new();
    private readonly List<Popup.Item> _popupItems = new();
    private double _peakTextAge;
    private float _peakTextDb = float.NaN;

    // Strip drag (reorder) state.
    private BusStrip? _dragStrip;
    private int _dragStripPointer;
    private double _dragStripStartX;
    private bool _dragStripActive;
    private BusStrip? _dragStripTarget;
    private bool _dragStripAfter;
    private double _lastNamePressTime = double.NegativeInfinity;
    private BusStrip? _lastNamePressStrip;

    private void RefreshStrips(Snapshot? editing)
    {
        MixerGraph mixer = Model.Mixer;
        // Drop strips whose bus disappeared.
        _stripOrder.Clear();
        foreach (KeyValuePair<DirgeId, BusStrip> pair in _strips)
        {
            if (MixerGraphOps.FindBus(mixer, pair.Key) is null) _stripOrder.Add(pair.Key);
        }
        for (int i = 0; i < _stripOrder.Count; i++)
        {
            _strips[_stripOrder[i]].Dispose();
            _strips.Remove(_stripOrder[i]);
        }
        _stripOrder.Clear();

        Bus? master = MixerGraphOps.Master(mixer);
        bool selectedBus = Selection.Kind == SelectionKind.Bus || Selection.Kind == SelectionKind.Effect;
        DirgeId selectedId = Selection.Kind == SelectionKind.Bus ? Selection.Id : Selection.Kind == SelectionKind.Effect ? Selection.Owner : DirgeId.None;
        int position = 0;
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            Bus bus = mixer.Buses[i];
            bool isMaster = ReferenceEquals(bus, master);
            if (!_strips.TryGetValue(bus.Id, out BusStrip? strip) || strip.IsMaster != isMaster)
            {
                strip?.Dispose();
                strip = CreateStrip(bus.Id, isMaster);
                _strips[bus.Id] = strip;
            }
            HTMLElement container = isMaster ? _masterSlot : _busStrips;
            if (isMaster)
            {
                if (!ReferenceEquals(strip.Root.ParentNode, container)) container.AppendChild(strip.Root);
            }
            else
            {
                // Keep DOM order equal to list order with minimal moves.
                NecronomiDom.Dom.Node? expected = ChildAt(container, position);
                if (!ReferenceEquals(expected, strip.Root)) container.InsertBefore(strip.Root, expected);
                position++;
            }
            Bus? parent = bus.Parent.IsNone ? null : MixerGraphOps.FindBus(mixer, bus.Parent);
            strip.Refresh(bus, parent, editing, selectedBus && selectedId == bus.Id);
            RefreshLfoButton(strip, bus);
            int selectedEffect = Selection.Kind == SelectionKind.Effect && Selection.Owner == bus.Id ? Selection.Index : -1;
            if (strip.EffectChainChanged(bus)) RebuildEffectRows(strip, bus, editing, selectedEffect);
            else RefreshEffectRows(strip, bus, editing, selectedEffect);
        }
    }

    private static NecronomiDom.Dom.Node? ChildAt(HTMLElement container, int index)
    {
        NecronomiDom.Dom.Node? child = container.FirstChild;
        for (int i = 0; i < index && child is not null; i++) child = child.NextSibling;
        return child;
    }

    private BusStrip CreateStrip(DirgeId id, bool isMaster)
    {
        Dom dom = _dom!;
        var strip = new BusStrip(dom, id, isMaster);

        dom.On(strip.Root, "pointerdown", _ => { if (Selection.Kind != SelectionKind.Bus || Selection.Id != id) Selection.Select(SelectionKind.Bus, id); });
        dom.On(strip.MuteButton, "click", () => ToggleBus(id, "Mute", b => b.Mute, (b, v) => b.Mute = v));
        dom.On(strip.SoloButton, "click", () => ToggleBus(id, "Solo", b => b.Solo, (b, v) => b.Solo = v));
        dom.On(strip.BypassButton, "click", () => ToggleBus(id, "Bypass effects", b => b.BypassEffects, (b, v) => b.BypassEffects = v));
        if (strip.DeleteButton is not null) dom.On(strip.DeleteButton, "click", () => DeleteBus(id));

        strip.Fader.ValueChanged += (value, continuous) =>
        {
            Bus? bus = MixerGraphOps.FindBus(Model.Mixer, id);
            if (bus is null) return;
            float before = bus.VolumeDb;
            float next = ClampDb(value);
            WriteProperty(strip.FaderToken, "Bus volume", PropertyRef.BusVolume(id), next, continuous, () => bus.VolumeDb = next, () => bus.VolumeDb = before, id);
        };
        strip.Fader.DragEnded += EndContinuous;
        dom.On(strip.Unscope, "pointerdown", e => e.StopPropagation());
        dom.On(strip.Unscope, "click", e =>
        {
            e.StopPropagation();
            Snapshot? editing = EditingSnapshot();
            if (editing is not null) UnscopeProperty(editing, PropertyRef.BusVolume(id));
        });

        if (!isMaster)
        {
            dom.On(strip.Route, "click", e => ShowRoutingPicker(strip, e));
            dom.On(strip.Name, "pointerdown", e => OnStripNamePointerDown(strip, e));
            dom.On(strip.Name, "pointermove", e => OnStripNamePointerMove(strip, e));
            dom.On(strip.Name, "pointerup", e => OnStripNamePointerUp(strip, e));
            dom.On(strip.Name, "pointercancel", e => OnStripNamePointerUp(strip, e));
            dom.On(strip.NameEdit, "keydown", e => OnRenameKey(strip, e));
            dom.On(strip.NameEdit, "blur", _ => CommitRename(strip));
            dom.On(strip.NameEdit, "pointerdown", e => e.StopPropagation());
        }

        dom.On(strip.FxAdd, "click", e => ShowAddEffectPopup(strip, e));
        dom.On(strip.LfoButton, "click", e => OpenLfoPopover(strip, e));
        dom.On(strip.MaxVoices, "change", () => CommitMaxVoices(strip));
        dom.On(strip.MaxVoices, "keydown", e => { if (e is KeyboardEvent k && k.Key == "Enter") CommitMaxVoices(strip); });
        return strip;
    }

    private void UpdateMeters(double delta)
    {
        IAudition audition = Audition;
        _peakTextAge += delta;
        // The peak readout is text: a changed string relayouts the document, so it follows the hold at 4 Hz in 0.5 dB steps.
        bool updatePeakText = _peakTextAge > 0.25;
        if (updatePeakText) _peakTextAge = 0;
        foreach (KeyValuePair<DirgeId, BusStrip> pair in _strips)
        {
            (float left, float right) = audition.BusPeakDb(pair.Key);
            pair.Value.UpdateMeter(left, right, delta);
            if (pair.Value.IsMaster && updatePeakText)
            {
                float hold = MathF.Round(pair.Value.HoldDb * 2f) / 2f;
                if (hold != _peakTextDb) { _peakTextDb = hold; Dom.SetText(pair.Value.PeakText, "peak " + Dom.Db(hold)); }
            }
        }
    }

    // ---- bus operations ------------------------------------------------------------------------------------------------

    private void ToggleBus(DirgeId id, string label, Func<Bus, bool> get, Action<Bus, bool> set)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, id);
        if (bus is null) return;
        bool before = get(bus);
        Project.Mutate(label, ChangeScope.Mixer, () => set(bus, !before), () => set(bus, before), id);
    }

    private void AddBus()
    {
        MixerGraph mixer = Model.Mixer;
        Bus? master = MixerGraphOps.Master(mixer);
        if (master is null) return;
        DirgeId parentId = master.Id;
        if (Selection.Kind == SelectionKind.Bus && MixerGraphOps.FindBus(mixer, Selection.Id) is not null) parentId = Selection.Id;
        else if (Selection.Kind == SelectionKind.Effect && MixerGraphOps.FindBus(mixer, Selection.Owner) is not null) parentId = Selection.Owner;
        var bus = new Bus { Name = MixerGraphOps.UniqueBusName(mixer, "Bus"), Parent = parentId };
        Project.Mutate("Add bus", ChangeScope.Mixer,
            () => { if (!mixer.Buses.Contains(bus)) mixer.Buses.Add(bus); },
            () => mixer.Buses.Remove(bus), bus.Id);
        Selection.Select(SelectionKind.Bus, bus.Id);
    }

    private void DeleteBus(DirgeId id)
    {
        AudioProject project = Model;
        MixerGraphOps.BusDeletion? plan = MixerGraphOps.PlanDeleteBus(project, id);
        if (plan is null) return;
        if (Selection.Kind == SelectionKind.Bus && Selection.Id == id) Selection.Select(SelectionKind.Bus, plan.NewParent);
        Project.Mutate("Delete bus", ChangeScope.Mixer | (plan.RemovedValues.Count > 0 ? ChangeScope.Snapshots : ChangeScope.None) | (plan.ReroutedEvents.Count > 0 ? ChangeScope.Events : ChangeScope.None),
            () => MixerGraphOps.ApplyDeleteBus(project, plan),
            () => MixerGraphOps.RevertDeleteBus(project, plan), id);
    }

    private void SetBusParent(DirgeId id, DirgeId parentId)
    {
        MixerGraph mixer = Model.Mixer;
        Bus? bus = MixerGraphOps.FindBus(mixer, id);
        if (bus is null || bus.Parent == parentId || MixerGraphOps.IsSelfOrDescendant(mixer, parentId, id)) return;
        DirgeId before = bus.Parent;
        Project.Mutate("Route bus", ChangeScope.Mixer, () => bus.Parent = parentId, () => bus.Parent = before, id);
    }

    private void MoveBus(DirgeId id, int to)
    {
        MixerGraph mixer = Model.Mixer;
        int from = MixerGraphOps.IndexOfBus(mixer, id);
        if (from < 0 || from == to) return;
        Project.Mutate("Reorder buses", ChangeScope.Mixer, () => MixerGraphOps.MoveBus(mixer, from, to), () => MixerGraphOps.MoveBus(mixer, to, from), id);
    }

    private void CommitMaxVoices(BusStrip strip)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, strip.Id);
        if (bus is null) return;
        if (!Dom.TryParse(strip.MaxVoices.Value, out float parsed))
        {
            strip.MaxVoices.Value = bus.MaxVoices.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        int value = Math.Max(0, (int)MathF.Round(parsed));
        int before = bus.MaxVoices;
        if (value == before) return;
        Project.Mutate("Max voices", ChangeScope.Mixer, () => bus.MaxVoices = value, () => bus.MaxVoices = before, strip.Id);
    }

    private void ShowRoutingPicker(BusStrip strip, Event e)
    {
        MixerGraph mixer = Model.Mixer;
        Bus? bus = MixerGraphOps.FindBus(mixer, strip.Id);
        if (bus is null) return;
        MixerGraphOps.RoutingTargets(mixer, strip.Id, _routingTargets);
        _popupItems.Clear();
        for (int i = 0; i < _routingTargets.Count; i++)
        {
            Bus target = _routingTargets[i];
            DirgeId targetId = target.Id;
            _popupItems.Add(new Popup.Item(target.Name, () => SetBusParent(strip.Id, targetId), current: target.Id == bus.Parent));
        }
        (double x, double y) = PopupAnchor(strip.Route, e);
        _popup.Show("OUTPUT", _popupItems, x, y);
    }

    /// <summary>Document position just below <paramref name="anchor"/>, falling back to the pointer position.</summary>
    private (double X, double Y) PopupAnchor(HTMLElement anchor, Event e)
    {
        if (_dom!.TryGetBounds(anchor, out Rect bounds)) return (bounds.X, bounds.Y + bounds.Height + 2);
        return e is MouseEvent m ? (m.ClientX, m.ClientY) : (0, 0);
    }

    // ---- rename ----------------------------------------------------------------------------------------------------------

    private bool IsDoublePress(BusStrip strip)
    {
        double now = Time.GetTicksMsec() / 1000.0;
        bool isDouble = ReferenceEquals(_lastNamePressStrip, strip) && now - _lastNamePressTime < 0.35;
        _lastNamePressTime = isDouble ? double.NegativeInfinity : now;
        _lastNamePressStrip = strip;
        return isDouble;
    }

    private void BeginRename(BusStrip strip)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, strip.Id);
        if (bus is null) return;
        strip.NameEdit.Value = bus.Name;
        strip.Name.Hidden = true;
        strip.NameEdit.Hidden = false;
        _host!.FlushPendingLayout(); // a hidden input is not focusable until it has layout
        strip.NameEdit.Focus();
        strip.NameEdit.Select();
    }

    private void OnRenameKey(BusStrip strip, Event e)
    {
        if (e is not KeyboardEvent key) return;
        if (key.Key == "Enter") { e.PreventDefault(); CommitRename(strip); }
        else if (key.Key == "Escape") { e.PreventDefault(); CancelRename(strip); }
    }

    private void CommitRename(BusStrip strip)
    {
        if (strip.NameEdit.Hidden) return;
        string name = strip.NameEdit.Value.Trim();
        CancelRename(strip);
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, strip.Id);
        if (bus is null || name.Length == 0 || name == bus.Name) return;
        string before = bus.Name;
        Project.Mutate("Rename bus", ChangeScope.Mixer, () => bus.Name = name, () => bus.Name = before, strip.Id);
    }

    private void CancelRename(BusStrip strip)
    {
        strip.NameEdit.Hidden = true;
        strip.Name.Hidden = false;
    }

    // ---- strip drag reorder ------------------------------------------------------------------------------------------------

    private void OnStripNamePointerDown(BusStrip strip, Event e)
    {
        if (e is not PointerEvent p || p.Button != 0) return;
        if (IsDoublePress(strip)) { Defer(() => BeginRename(strip)); return; }
        _dragStrip = strip;
        _dragStripPointer = p.PointerId;
        _dragStripStartX = p.ClientX;
        _dragStripActive = false;
        strip.Name.SetPointerCapture(p.PointerId);
    }

    private void OnStripNamePointerMove(BusStrip strip, Event e)
    {
        if (_dragStrip is null || !ReferenceEquals(_dragStrip, strip) || e is not PointerEvent p) return;
        if (!_dragStripActive)
        {
            if (Math.Abs(p.ClientX - _dragStripStartX) < 6) return;
            _dragStripActive = true;
            _popup.Hide();
        }
        ClearStripDropMarks();
        HTMLElement? under = Dom.Closest(_dom!.HitTest(p.ClientX, p.ClientY), "strip");
        _dragStripTarget = null;
        if (under is null || under.ClassList.Contains("master") || ReferenceEquals(under, strip.Root)) return;
        string? busText = under.Dataset["bus"];
        if (busText is null || !_strips.TryGetValue(DirgeId.Parse(busText), out BusStrip? target)) return;
        _dragStripTarget = target;
        _dragStripAfter = _dom.TryGetBounds(target.Root, out Rect bounds) && p.ClientX > bounds.X + bounds.Width / 2;
        Dom.SetClass(target.Root, _dragStripAfter ? "drop-after" : "drop-before", true);
    }

    private void OnStripNamePointerUp(BusStrip strip, Event e)
    {
        if (_dragStrip is null || !ReferenceEquals(_dragStrip, strip)) return;
        if (strip.Name.HasPointerCapture(_dragStripPointer)) strip.Name.ReleasePointerCapture(_dragStripPointer);
        ClearStripDropMarks();
        BusStrip? target = _dragStripTarget;
        bool active = _dragStripActive;
        _dragStrip = null; _dragStripTarget = null; _dragStripActive = false;
        if (!active || target is null) return;
        MixerGraph mixer = Model.Mixer;
        int from = MixerGraphOps.IndexOfBus(mixer, strip.Id);
        int to = MixerGraphOps.IndexOfBus(mixer, target.Id);
        if (from < 0 || to < 0) return;
        if (_dragStripAfter && from > to) to++;
        else if (!_dragStripAfter && from < to) to--;
        MoveBus(strip.Id, to);
    }

    private void ClearStripDropMarks()
    {
        foreach (KeyValuePair<DirgeId, BusStrip> pair in _strips)
        {
            Dom.SetClass(pair.Value.Root, "drop-before", false);
            Dom.SetClass(pair.Value.Root, "drop-after", false);
        }
    }
}
