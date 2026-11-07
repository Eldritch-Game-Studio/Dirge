#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using NecronomiDom.Dom;

namespace Dirge.Tool.Mixer;

public sealed partial class MixerPane
{
    private sealed class VcaRow
    {
        public DirgeId Id { get; }
        public HTMLElement Root { get; }
        public HTMLElement Name { get; }
        public HTMLInputElement NameEdit { get; }
        public HTMLElement Remove { get; }
        public Slider Volume { get; }
        public HTMLElement DbText { get; }
        public HTMLElement Buses { get; }
        public object Token { get; } = new();

        public VcaRow(Dom dom, DirgeId id)
        {
            Id = id;
            Root = dom.El("div", "vca-row");
            HTMLElement head = dom.El(Root, "div", "row-head");
            Name = dom.El(head, "div", "row-name", "");
            Name.Title = "Double-click to rename";
            NameEdit = dom.Input(head, "name-edit", "");
            NameEdit.Hidden = true;
            Remove = dom.El(head, "button", "strip-x", "x");
            Remove.Title = "Remove VCA";
            HTMLElement line = dom.El(Root, "div", "row-line");
            HTMLElement track = dom.El(line, "div", "hslider");
            HTMLElement fill = dom.El(track, "div", "hfill");
            HTMLElement thumb = dom.El(track, "div", "hthumb");
            Volume = new Slider(dom, track, thumb, fill, vertical: false, FaderMinDb, FaderMaxDb, HSliderLengthPx, HSliderThumbPx) { WheelStep = 0.5f, ResetValue = 0f };
            DbText = dom.El(line, "div", "field-value", "0.0 dB");
            Buses = dom.El(line, "button", "pill", "Buses");
            Buses.Title = "Assign this VCA to buses";
        }

        public void Dispose() => Root.Remove();
    }

    private sealed class SnapshotRow
    {
        public DirgeId Id { get; }
        public HTMLElement Root { get; }
        public HTMLElement Name { get; }
        public HTMLInputElement NameEdit { get; }
        public HTMLElement Remove { get; }
        public HTMLInputElement Priority { get; }
        public HTMLElement Mode { get; }
        public HTMLElement Count { get; }

        public SnapshotRow(Dom dom, DirgeId id)
        {
            Id = id;
            Root = dom.El("div", "snap-row");
            HTMLElement head = dom.El(Root, "div", "row-head");
            Name = dom.El(head, "div", "row-name", "");
            Name.Title = "Click to edit this snapshot in the mixer; double-click to rename";
            NameEdit = dom.Input(head, "name-edit", "");
            NameEdit.Hidden = true;
            Remove = dom.El(head, "button", "strip-x", "x");
            Remove.Title = "Remove snapshot";
            HTMLElement line = dom.El(Root, "div", "row-line");
            dom.El(line, "div", "field-label", "Priority");
            Priority = dom.Input(line, "num", "0");
            Mode = dom.El(line, "button", "pill", "Blending");
            Mode.Title = "Blending: scoped values are interpolated by intensity. Overriding: replaces lower-priority snapshots.";
            Count = dom.El(line, "div", "field-label", "");
        }

        public void Dispose() => Root.Remove();
    }

    private readonly Dictionary<DirgeId, VcaRow> _vcaRows = new();
    private readonly Dictionary<DirgeId, SnapshotRow> _snapshotRows = new();
    private readonly List<DirgeId> _rowScratch = new();
    private HTMLElement _vcaList = null!;
    private HTMLElement _snapList = null!;
    private HTMLElement _snapEditor = null!;
    private HTMLElement _snapEditorTitle = null!;
    private HTMLElement? _vcaEmpty, _snapEmpty;
    private Slider _intensity = null!;
    private HTMLElement _intensityText = null!;
    private HTMLInputElement _attack = null!, _hold = null!, _decay = null!, _sustain = null!, _release = null!;
    private double _lastRowPressTime = double.NegativeInfinity;
    private object? _lastRowPressTarget;

    // Snapshot audition.
    private SnapshotInstance? _snapshotInstance;
    private DirgeId _snapshotAuditionId;
    private float _snapshotIntensity;
    private bool _snapshotAuditionDirty;

    private void BindSide()
    {
        Dom dom = _dom!;
        _vcaList = dom.Required("vca-list");
        _snapList = dom.Required("snap-list");
        _snapEditor = dom.Required("snap-editor");
        _snapEditorTitle = dom.Required("snap-editor-title");
        dom.On(dom.Required("vca-add"), "click", AddVca);
        dom.On(dom.Required("snap-add"), "click", AddSnapshot);
        dom.On(dom.Required("snap-capture"), "click", CaptureSnapshot);
        dom.On(dom.Required("snap-done"), "click", LeaveSnapshotEditing);

        HTMLElement intensityTrack = dom.Required("snap-intensity");
        HTMLElement intensityThumb = intensityTrack.QuerySelector(".hthumb") as HTMLElement ?? throw new InvalidOperationException("mixer.html: #snap-intensity needs .hthumb");
        HTMLElement intensityFill = intensityTrack.QuerySelector(".hfill") as HTMLElement ?? throw new InvalidOperationException("mixer.html: #snap-intensity needs .hfill");
        _intensity = new Slider(dom, intensityTrack, intensityThumb, intensityFill, vertical: false, 0f, 1f, HSliderLengthPx, HSliderThumbPx) { WheelStep = 0.05f, ResetValue = 0f };
        _intensity.ValueChanged += (value, _) => SetSnapshotIntensity(value);
        _intensityText = dom.Required("snap-intensity-text");

        _attack = (HTMLInputElement)dom.Required("snap-attack");
        _hold = (HTMLInputElement)dom.Required("snap-hold");
        _decay = (HTMLInputElement)dom.Required("snap-decay");
        _sustain = (HTMLInputElement)dom.Required("snap-sustain");
        _release = (HTMLInputElement)dom.Required("snap-release");
        WireAhdsr(_attack, "Attack", a => a.Attack, (a, v) => a.Attack = Math.Max(0f, v));
        WireAhdsr(_hold, "Hold", a => a.Hold, (a, v) => a.Hold = Math.Max(0f, v));
        WireAhdsr(_decay, "Decay", a => a.Decay, (a, v) => a.Decay = Math.Max(0f, v));
        WireAhdsr(_sustain, "Sustain", a => a.Sustain, (a, v) => a.Sustain = Math.Clamp(v, 0f, 1f));
        WireAhdsr(_release, "Release", a => a.Release, (a, v) => a.Release = Math.Max(0f, v));
    }

    private void UnbindSide()
    {
        foreach (VcaRow row in _vcaRows.Values) row.Dispose();
        foreach (SnapshotRow row in _snapshotRows.Values) row.Dispose();
        _vcaRows.Clear();
        _snapshotRows.Clear();
    }

    // ---- VCAs ----------------------------------------------------------------------------------------------------------------

    private void RefreshVcaList()
    {
        MixerGraph mixer = Model.Mixer;
        _rowScratch.Clear();
        foreach (KeyValuePair<DirgeId, VcaRow> pair in _vcaRows)
        {
            if (MixerGraphOps.FindVca(mixer, pair.Key) is null) _rowScratch.Add(pair.Key);
        }
        for (int i = 0; i < _rowScratch.Count; i++) { _vcaRows[_rowScratch[i]].Dispose(); _vcaRows.Remove(_rowScratch[i]); }

        if (mixer.Vcas.Count == 0)
        {
            if (_vcaEmpty is null) { _vcaEmpty = _dom!.El("div", "empty", "No VCAs. Add one to group bus volumes."); _vcaList.AppendChild(_vcaEmpty); }
        }
        else if (_vcaEmpty is not null) { _vcaEmpty.Remove(); _vcaEmpty = null; }

        for (int i = 0; i < mixer.Vcas.Count; i++)
        {
            Vca vca = mixer.Vcas[i];
            if (!_vcaRows.TryGetValue(vca.Id, out VcaRow? row))
            {
                row = CreateVcaRow(vca.Id);
                _vcaRows[vca.Id] = row;
            }
            NecronomiDom.Dom.Node? expected = ChildAt(_vcaList, i);
            if (!ReferenceEquals(expected, row.Root)) _vcaList.InsertBefore(row.Root, expected);
            Dom.SetText(row.Name, vca.Name);
            if (!row.Volume.IsDragging) row.Volume.SetValue(vca.VolumeDb);
            Dom.SetText(row.DbText, Dom.Db(vca.VolumeDb));
            int assigned = 0;
            for (int b = 0; b < mixer.Buses.Count; b++) if (mixer.Buses[b].Vcas.Contains(vca.Id)) assigned++;
            Dom.SetText(row.Buses, assigned == 0 ? "Buses" : assigned == 1 ? "1 bus" : assigned.ToString(CultureInfo.InvariantCulture) + " buses");
            Dom.SetClass(row.Buses, "on", assigned > 0);
        }
    }

    private VcaRow CreateVcaRow(DirgeId id)
    {
        Dom dom = _dom!;
        var row = new VcaRow(dom, id);
        row.Volume.ValueChanged += (value, continuous) =>
        {
            Vca? vca = MixerGraphOps.FindVca(Model.Mixer, id);
            if (vca is null) return;
            float before = vca.VolumeDb;
            float next = ClampDb(value);
            Action apply = () => vca.VolumeDb = next;
            Action revert = () => vca.VolumeDb = before;
            if (continuous) Project.MutateContinuous(row.Token, "VCA volume", ChangeScope.Mixer, apply, revert, id);
            else Project.Mutate("VCA volume", ChangeScope.Mixer, apply, revert, id);
        };
        row.Volume.DragEnded += EndContinuous;
        dom.On(row.Remove, "click", () => RemoveVca(id));
        dom.On(row.Buses, "click", e => ShowVcaBusPicker(row, e));
        dom.On(row.Name, "pointerdown", _ => { if (IsDoubleRowPress(row)) Defer(() => BeginRowRename(row.Name, row.NameEdit, MixerGraphOps.FindVca(Model.Mixer, id)?.Name ?? "")); });
        dom.On(row.NameEdit, "keydown", e => OnRowRenameKey(e, row.Name, row.NameEdit, name => RenameVca(id, name)));
        dom.On(row.NameEdit, "blur", _ => CommitRowRename(row.Name, row.NameEdit, name => RenameVca(id, name)));
        return row;
    }

    private void AddVca()
    {
        MixerGraph mixer = Model.Mixer;
        var vca = new Vca { Name = MixerGraphOps.UniqueVcaName(mixer, "VCA") };
        Project.Mutate("Add VCA", ChangeScope.Mixer, () => { if (!mixer.Vcas.Contains(vca)) mixer.Vcas.Add(vca); }, () => mixer.Vcas.Remove(vca), vca.Id);
        Selection.Select(SelectionKind.Vca, vca.Id);
    }

    private void RemoveVca(DirgeId id)
    {
        MixerGraph mixer = Model.Mixer;
        Vca? vca = MixerGraphOps.FindVca(mixer, id);
        if (vca is null) return;
        int index = mixer.Vcas.IndexOf(vca);
        var assignedBuses = new List<Bus>();
        for (int b = 0; b < mixer.Buses.Count; b++) if (mixer.Buses[b].Vcas.Contains(id)) assignedBuses.Add(mixer.Buses[b]);
        if (Selection.Kind == SelectionKind.Vca && Selection.Id == id) Selection.Clear();
        Project.Mutate("Remove VCA", ChangeScope.Mixer,
            () =>
            {
                mixer.Vcas.Remove(vca);
                for (int b = 0; b < assignedBuses.Count; b++) assignedBuses[b].Vcas.Remove(id);
            },
            () =>
            {
                if (!mixer.Vcas.Contains(vca)) mixer.Vcas.Insert(Math.Min(index, mixer.Vcas.Count), vca);
                for (int b = 0; b < assignedBuses.Count; b++) if (!assignedBuses[b].Vcas.Contains(id)) assignedBuses[b].Vcas.Add(id);
            }, id);
    }

    private void RenameVca(DirgeId id, string name)
    {
        Vca? vca = MixerGraphOps.FindVca(Model.Mixer, id);
        if (vca is null || name.Length == 0 || name == vca.Name) return;
        string before = vca.Name;
        Project.Mutate("Rename VCA", ChangeScope.Mixer, () => vca.Name = name, () => vca.Name = before, id);
    }

    private void ShowVcaBusPicker(VcaRow row, Event e)
    {
        MixerGraph mixer = Model.Mixer;
        _popupItems.Clear();
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            Bus bus = mixer.Buses[i];
            bool on = bus.Vcas.Contains(row.Id);
            _popupItems.Add(new Popup.Item((on ? "[x] " : "[ ] ") + bus.Name, () => ToggleVcaAssignment(bus.Id, row.Id), on: on));
        }
        (double x, double y) = PopupAnchor(row.Buses, e);
        _popup.Show("ASSIGN TO BUSES", _popupItems, x, y, keepOpenOnSelect: false);
    }

    private void ToggleVcaAssignment(DirgeId busId, DirgeId vcaId)
    {
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, busId);
        if (bus is null) return;
        bool had = bus.Vcas.Contains(vcaId);
        Project.Mutate(had ? "Unassign VCA" : "Assign VCA", ChangeScope.Mixer,
            () => { if (had) bus.Vcas.Remove(vcaId); else if (!bus.Vcas.Contains(vcaId)) bus.Vcas.Add(vcaId); },
            () => { if (had) { if (!bus.Vcas.Contains(vcaId)) bus.Vcas.Add(vcaId); } else bus.Vcas.Remove(vcaId); }, busId);
    }

    // ---- snapshots -----------------------------------------------------------------------------------------------------------

    private void RefreshSnapshotList(Snapshot? editing)
    {
        List<Snapshot> snapshots = Model.Snapshots;
        _rowScratch.Clear();
        foreach (KeyValuePair<DirgeId, SnapshotRow> pair in _snapshotRows)
        {
            bool exists = false;
            for (int i = 0; i < snapshots.Count; i++) if (snapshots[i].Id == pair.Key) { exists = true; break; }
            if (!exists) _rowScratch.Add(pair.Key);
        }
        for (int i = 0; i < _rowScratch.Count; i++) { _snapshotRows[_rowScratch[i]].Dispose(); _snapshotRows.Remove(_rowScratch[i]); }

        if (snapshots.Count == 0)
        {
            if (_snapEmpty is null) { _snapEmpty = _dom!.El("div", "empty", "No snapshots. Add one, select it, then move faders to scope values."); _snapList.AppendChild(_snapEmpty); }
        }
        else if (_snapEmpty is not null) { _snapEmpty.Remove(); _snapEmpty = null; }

        for (int i = 0; i < snapshots.Count; i++)
        {
            Snapshot snapshot = snapshots[i];
            if (!_snapshotRows.TryGetValue(snapshot.Id, out SnapshotRow? row))
            {
                row = CreateSnapshotRow(snapshot.Id);
                _snapshotRows[snapshot.Id] = row;
            }
            NecronomiDom.Dom.Node? expected = ChildAt(_snapList, i);
            if (!ReferenceEquals(expected, row.Root)) _snapList.InsertBefore(row.Root, expected);
            Dom.SetText(row.Name, snapshot.Name);
            Dom.SetClass(row.Root, "selected", ReferenceEquals(snapshot, editing));
            if (!ReferenceEquals(row.Priority.OwnerDocument?.FocusedElement, row.Priority))
            {
                string priority = snapshot.Priority.ToString(CultureInfo.InvariantCulture);
                if (row.Priority.Value != priority) row.Priority.Value = priority;
            }
            Dom.SetText(row.Mode, snapshot.Mode == SnapshotMode.Blending ? "Blending" : "Overriding");
            Dom.SetClass(row.Mode, "on", snapshot.Mode == SnapshotMode.Overriding);
            Dom.SetText(row.Count, snapshot.Values.Count == 1 ? "1 value" : snapshot.Values.Count.ToString(CultureInfo.InvariantCulture) + " values");
        }

        RefreshSnapshotEditor(editing);
    }

    private SnapshotRow CreateSnapshotRow(DirgeId id)
    {
        Dom dom = _dom!;
        var row = new SnapshotRow(dom, id);
        dom.On(row.Root, "pointerdown", e =>
        {
            if (e.Target is HTMLElement target && (target.LocalName == "input" || target.LocalName == "button")) return;
            if (Selection.Kind != SelectionKind.Snapshot || Selection.Id != id) Selection.Select(SelectionKind.Snapshot, id);
        });
        dom.On(row.Name, "pointerdown", _ => { if (IsDoubleRowPress(row)) Defer(() => BeginRowRename(row.Name, row.NameEdit, FindSnapshot(id)?.Name ?? "")); });
        dom.On(row.NameEdit, "keydown", e => OnRowRenameKey(e, row.Name, row.NameEdit, name => RenameSnapshot(id, name)));
        dom.On(row.NameEdit, "blur", _ => CommitRowRename(row.Name, row.NameEdit, name => RenameSnapshot(id, name)));
        dom.On(row.Remove, "click", e => { e.StopPropagation(); RemoveSnapshot(id); });
        dom.On(row.Mode, "click", e => { e.StopPropagation(); ToggleSnapshotMode(id); });
        dom.On(row.Priority, "change", () => CommitPriority(row));
        dom.On(row.Priority, "keydown", e => { if (e is KeyboardEvent k && k.Key == "Enter") { e.PreventDefault(); CommitPriority(row); } });
        return row;
    }

    private Snapshot? FindSnapshot(DirgeId id)
    {
        List<Snapshot> snapshots = Model.Snapshots;
        for (int i = 0; i < snapshots.Count; i++) if (snapshots[i].Id == id) return snapshots[i];
        return null;
    }

    private void AddSnapshot()
    {
        List<Snapshot> snapshots = Model.Snapshots;
        var snapshot = new Snapshot { Name = MixerGraphOps.UniqueSnapshotName(snapshots, "Snapshot") };
        Project.Mutate("Add snapshot", ChangeScope.Snapshots, () => { if (!snapshots.Contains(snapshot)) snapshots.Add(snapshot); }, () => snapshots.Remove(snapshot), snapshot.Id);
        Selection.Select(SelectionKind.Snapshot, snapshot.Id);
    }

    private void RemoveSnapshot(DirgeId id)
    {
        List<Snapshot> snapshots = Model.Snapshots;
        Snapshot? snapshot = FindSnapshot(id);
        if (snapshot is null) return;
        int index = snapshots.IndexOf(snapshot);
        if (_editingSnapshotId == id) LeaveSnapshotEditing();
        else if (Selection.Kind == SelectionKind.Snapshot && Selection.Id == id) Selection.Clear();
        Project.Mutate("Remove snapshot", ChangeScope.Snapshots,
            () => snapshots.Remove(snapshot),
            () => { if (!snapshots.Contains(snapshot)) snapshots.Insert(Math.Min(index, snapshots.Count), snapshot); }, id);
    }

    private void RenameSnapshot(DirgeId id, string name)
    {
        Snapshot? snapshot = FindSnapshot(id);
        if (snapshot is null || name.Length == 0 || name == snapshot.Name) return;
        string before = snapshot.Name;
        Project.Mutate("Rename snapshot", ChangeScope.Snapshots, () => snapshot.Name = name, () => snapshot.Name = before, id);
    }

    private void ToggleSnapshotMode(DirgeId id)
    {
        Snapshot? snapshot = FindSnapshot(id);
        if (snapshot is null) return;
        SnapshotMode before = snapshot.Mode;
        SnapshotMode next = before == SnapshotMode.Blending ? SnapshotMode.Overriding : SnapshotMode.Blending;
        Project.Mutate("Snapshot mode", ChangeScope.Snapshots, () => snapshot.Mode = next, () => snapshot.Mode = before, id);
    }

    private void CommitPriority(SnapshotRow row)
    {
        Snapshot? snapshot = FindSnapshot(row.Id);
        if (snapshot is null) return;
        if (!Dom.TryParse(row.Priority.Value, out float parsed)) { row.Priority.Value = snapshot.Priority.ToString(CultureInfo.InvariantCulture); return; }
        int value = (int)MathF.Round(parsed);
        int before = snapshot.Priority;
        if (value == before) return;
        Project.Mutate("Snapshot priority", ChangeScope.Snapshots, () => snapshot.Priority = value, () => snapshot.Priority = before, row.Id);
    }

    /// <summary>Scope every bus volume at its authored value so the snapshot can be shaped from the current mix.</summary>
    private void CaptureSnapshot()
    {
        Snapshot? snapshot = EditingSnapshot();
        if (snapshot is null) return;
        MixerGraph mixer = Model.Mixer;
        var added = new List<SnapshotValue>();
        for (int i = 0; i < mixer.Buses.Count; i++)
        {
            Bus bus = mixer.Buses[i];
            PropertyRef target = PropertyRef.BusVolume(bus.Id);
            if (!MixerGraphOps.IsScoped(snapshot, target)) added.Add(new SnapshotValue { Target = target, Value = bus.VolumeDb });
        }
        if (added.Count == 0) return;
        Project.Mutate("Capture snapshot", ChangeScope.Snapshots,
            () => { for (int i = 0; i < added.Count; i++) if (!snapshot.Values.Contains(added[i])) snapshot.Values.Add(added[i]); },
            () => { for (int i = 0; i < added.Count; i++) snapshot.Values.Remove(added[i]); }, snapshot.Id);
    }

    private void RefreshSnapshotEditor(Snapshot? editing)
    {
        if (editing is null)
        {
            _snapEditor.Hidden = true;
            StopSnapshotAudition();
            return;
        }
        if (_snapshotAuditionId != editing.Id)
        {
            StopSnapshotAudition();
            _snapshotAuditionId = editing.Id;
            _snapshotIntensity = 0f;
            _intensity.SetValue(0f);
            Dom.SetText(_intensityText, "0 %");
        }
        _snapEditor.Hidden = false;
        Dom.SetText(_snapEditorTitle, editing.Name);
        Ahdsr envelope = editing.Intensity;
        SetIfUnfocused(_attack, Dom.Num(envelope.Attack, 0.01f));
        SetIfUnfocused(_hold, Dom.Num(envelope.Hold, 0.01f));
        SetIfUnfocused(_decay, Dom.Num(envelope.Decay, 0.01f));
        SetIfUnfocused(_sustain, Dom.Num(envelope.Sustain, 0.01f));
        SetIfUnfocused(_release, Dom.Num(envelope.Release, 0.01f));
    }

    private static void SetIfUnfocused(HTMLInputElement input, string text)
    {
        if (ReferenceEquals(input.OwnerDocument?.FocusedElement, input)) return;
        if (input.Value != text) input.Value = text;
    }

    private void WireAhdsr(HTMLInputElement input, string label, Func<Ahdsr, float> get, Action<Ahdsr, float> set)
    {
        Dom dom = _dom!;
        void Commit()
        {
            Snapshot? snapshot = EditingSnapshot();
            if (snapshot is null) return;
            Ahdsr envelope = snapshot.Intensity;
            if (!Dom.TryParse(input.Value, out float value)) { input.Value = Dom.Num(get(envelope), 0.01f); return; }
            float before = get(envelope);
            if (value == before) return;
            Project.Mutate("Snapshot " + label, ChangeScope.Snapshots, () => set(envelope, value), () => set(envelope, before), snapshot.Id);
        }
        dom.On(input, "change", Commit);
        dom.On(input, "keydown", e => { if (e is KeyboardEvent k && k.Key == "Enter") { e.PreventDefault(); Commit(); } });
    }

    // ---- snapshot audition ---------------------------------------------------------------------------------------------------

    private void SetSnapshotIntensity(float value)
    {
        _snapshotIntensity = Math.Clamp(value, 0f, 1f);
        Dom.SetText(_intensityText, ((int)MathF.Round(_snapshotIntensity * 100f)).ToString(CultureInfo.InvariantCulture) + " %");
        _snapshotAuditionDirty = true;
    }

    private void TickSnapshotAudition(double delta)
    {
        if (!_snapshotAuditionDirty) return;
        Snapshot? editing = EditingSnapshot();
        if (editing is null) { StopSnapshotAudition(); _snapshotAuditionDirty = false; return; }
        DirgeSystem? system = Audition.System;
        if (system is null) return; // rebuilding: retry next tick
        if (_snapshotIntensity <= 0f)
        {
            if (_snapshotInstance is not null) { _snapshotInstance.Stop(); _snapshotInstance = null; }
            _snapshotAuditionDirty = false;
            return;
        }
        if (_snapshotInstance is null || !_snapshotInstance.IsActive || _snapshotInstance.Snapshot.Id != editing.Id)
        {
            _snapshotInstance?.Stop();
            _snapshotInstance = system.Mixer.StartSnapshot(editing.Name);
        }
        _snapshotInstance.Intensity = _snapshotIntensity;
        _snapshotAuditionDirty = false;
    }

    private void StopSnapshotAudition()
    {
        if (_snapshotInstance is not null)
        {
            _snapshotInstance.Stop();
            _snapshotInstance = null;
        }
        _snapshotAuditionId = DirgeId.None;
        _snapshotIntensity = 0f;
    }

    // ---- inline rename shared by rows -----------------------------------------------------------------------------------------

    private bool IsDoubleRowPress(object target)
    {
        double now = Godot.Time.GetTicksMsec() / 1000.0;
        bool isDouble = ReferenceEquals(_lastRowPressTarget, target) && now - _lastRowPressTime < 0.35;
        _lastRowPressTime = isDouble ? double.NegativeInfinity : now;
        _lastRowPressTarget = target;
        return isDouble;
    }

    private void BeginRowRename(HTMLElement name, HTMLInputElement edit, string current)
    {
        edit.Value = current;
        name.Hidden = true;
        edit.Hidden = false;
        _host!.FlushPendingLayout(); // a hidden input is not focusable until it has layout
        edit.Focus();
        edit.Select();
    }

    private static void OnRowRenameKey(Event e, HTMLElement name, HTMLInputElement edit, Action<string> commit)
    {
        if (e is not KeyboardEvent key) return;
        if (key.Key == "Enter") { e.PreventDefault(); CommitRowRename(name, edit, commit); }
        else if (key.Key == "Escape") { e.PreventDefault(); edit.Hidden = true; name.Hidden = false; }
    }

    private static void CommitRowRename(HTMLElement name, HTMLInputElement edit, Action<string> commit)
    {
        if (edit.Hidden) return;
        string value = edit.Value.Trim();
        edit.Hidden = true;
        name.Hidden = false;
        commit(value);
    }
}
