#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Mixer;

/// <summary>
/// FMOD-style mixer: bus strips (fader, stereo peak meter, mute/solo/bypass, routing, effect chain, max voices), VCAs and
/// snapshots in a side pane. One NecronomiDOM document (<c>mixer.html</c>); every edit goes through
/// <see cref="ProjectDocument"/> with <see cref="ChangeScope.Mixer"/> or <see cref="ChangeScope.Snapshots"/>.
/// Document updates are incremental: strips/rows are reconciled by id, meters touch inline styles only.
/// </summary>
public sealed partial class MixerPane : IToolPane, IToolPaneCommands
{
    /// <summary>Root control; forwards process ticks so meters animate without the pane being a node.</summary>
    private sealed partial class PaneRoot : Control
    {
        public Action<double>? Tick;
        public override void _Process(double delta) => Tick?.Invoke(delta);
    }

    private const float FaderMinDb = -80f;
    private const float FaderMaxDb = 12f;
    private const int FaderLengthPx = 150;
    private const int FaderThumbPx = 12;
    private const int MeterInnerPx = 148;
    private const int HSliderLengthPx = 118;
    private const int HSliderWideLengthPx = 148;
    private const int HSliderThumbPx = 8;

    private readonly PaneRoot _root = new() { Name = "MixerPane" };
    private ToolContext? _context;
    private DocumentHost? _host;
    private Dom? _dom;
    private bool _bound;
    private bool _refreshQueued;
    private ChangeScope _pendingScope;

    private Popup _popup = null!;
    private HTMLElement _masterSlot = null!;
    private HTMLElement _busStrips = null!;
    private HTMLElement _snapBanner = null!;

    public string Title => "Mixer";
    public Control Root => _root;

    private ProjectDocument Project => _context!.Project;
    private AudioProject Model => _context!.Project.Project;
    private Selection Selection => _context!.Selection;
    private IAudition Audition => _context!.Audition;

    /// <summary>Shell keyboard routing: Delete removes the selected bus/effect/VCA/snapshot, Escape closes popups or leaves
    /// snapshot editing. Undo/redo/save/play are the shell's.</summary>
    public bool Handle(ToolCommand command)
    {
        if (_context is null || !_bound) return false;
        switch (command)
        {
            case ToolCommand.Escape:
                if (_popup.IsOpen) { _popup.Hide(); return true; }
                if (!_popover.Hidden) { ClosePopover(); return true; }
                if (!_lfoPopover.Hidden) { CloseLfoPopover(); return true; }
                if (!_editingSnapshotId.IsNone) { LeaveSnapshotEditing(); return true; }
                return false;
            case ToolCommand.Delete:
                if (_dom!.Document.FocusedElement is HTMLInputElement) return false; // text editing owns Delete
                switch (Selection.Kind)
                {
                    case SelectionKind.Bus: DeleteBus(Selection.Id); return true;
                    case SelectionKind.Effect:
                    {
                        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, Selection.Owner);
                        if (bus is null) return false;
                        for (int i = 0; i < bus.Effects.Count; i++)
                        {
                            if (bus.Effects[i].Id == Selection.Id) { RemoveEffect(bus.Id, i); return true; }
                        }
                        return false;
                    }
                    case SelectionKind.Vca: RemoveVca(Selection.Id); return true;
                    case SelectionKind.Snapshot: RemoveSnapshot(Selection.Id); return true;
                    default: return false;
                }
            default:
                return false;
        }
    }

    public void Attach(ToolContext context)
    {
        Detach();
        _context = context;
        _root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _root.ClipContents = true;
        _host = ToolDocuments.Create("mixer.html");
        _root.AddChild(_host);
        context.Project.Changed += OnProjectChanged;
        context.Selection.Changed += OnSelectionChanged;
        context.Audition.Rebuilt += OnAuditionRebuilt;
        _root.Tick = Tick;
    }

    public void Detach()
    {
        if (_context is null) return;
        _root.Tick = null;
        _context.Project.Changed -= OnProjectChanged;
        _context.Selection.Changed -= OnSelectionChanged;
        _context.Audition.Rebuilt -= OnAuditionRebuilt;
        StopSnapshotAudition();
        _editingSnapshotId = DirgeId.None;
        Unbind();
        if (_host is not null)
        {
            _root.RemoveChild(_host);
            _host.QueueFree();
            _host = null;
        }
        _context = null;
    }

    private void Tick(double delta)
    {
        if (_context is null || _host is null) return;
        if (!_bound)
        {
            if (_host.Document is null) return;
            Bind();
        }
        if (_refreshQueued)
        {
            _refreshQueued = false;
            ChangeScope scope = _pendingScope;
            _pendingScope = ChangeScope.None;
            Refresh(scope);
        }
        if (_deferred is not null)
        {
            Action deferred = _deferred;
            _deferred = null;
            deferred();
        }
        if (_root.IsVisibleInTree()) UpdateMeters(delta); // hidden bottom tab: no DOM writes nobody can see
        TickSnapshotAudition(delta);
    }

    /// <summary>Work that must run after the current pointer sequence has finished dispatching (e.g. the host's own
    /// mousedown focus handling would immediately blur an input we focus from inside a pointerdown listener).</summary>
    private Action? _deferred;
    private void Defer(Action action) => _deferred = action;

    // ---- binding -----------------------------------------------------------------------------------------------------

    private void Bind()
    {
        _dom = new Dom(_host!);
        _masterSlot = _dom.Required("master-slot");
        _busStrips = _dom.Required("bus-strips");
        _snapBanner = _dom.Required("snap-banner");
        _popup = new Popup(_dom, _dom.Required("popup"));
        _dom.On(_dom.Required("bus-add"), "click", AddBus);
        BindEffects();
        BindSide();
        BindModulators();
        _bound = true;
        Refresh(ChangeScope.All);
    }

    private void Unbind()
    {
        if (!_bound) return;
        _bound = false;
        UnbindEffects();
        UnbindSide();
        UnbindModulators();
        foreach (BusStrip strip in _strips.Values) strip.Dispose();
        _strips.Clear();
        _stripOrder.Clear();
        _dom?.DetachAll();
        _dom = null;
    }

    private void OnProjectChanged(ChangeScope scope, DirgeId subject)
    {
        // Coalesce: a drag produces many Changed events per frame; refresh once at the next tick.
        _pendingScope |= scope;
        _refreshQueued = true;
    }

    private void OnSelectionChanged()
    {
        // Selecting a snapshot anywhere in the tool enters editing mode; selecting buses/effects afterwards keeps it
        // (FMOD behaviour), so a fader press that also selects its strip still writes the snapshot. "Done" leaves it.
        if (Selection.Kind == SelectionKind.Snapshot) _editingSnapshotId = Selection.Id;
        _pendingScope |= ChangeScope.Snapshots | ChangeScope.Mixer;
        _refreshQueued = true;
    }

    private void OnAuditionRebuilt()
    {
        _snapshotInstance = null;
        _snapshotAuditionDirty = true;
    }

    private void Refresh(ChangeScope scope)
    {
        if (!_bound || _context is null) return;
        bool mixer = (scope & (ChangeScope.Mixer | ChangeScope.All)) != 0;
        bool snapshots = (scope & (ChangeScope.Snapshots | ChangeScope.All)) != 0;
        Snapshot? editing = EditingSnapshot();
        if (snapshots) RefreshSnapshotList(editing);
        if (mixer || snapshots)
        {
            RefreshStrips(editing);
            RefreshVcaList();
            RefreshPopover(editing);
            RefreshLfoPopover();
        }
        _snapBanner.Hidden = editing is null;
        if (editing is not null) Dom.SetText(_snapBanner, "Editing snapshot: " + editing.Name);
    }

    // ---- snapshot editing mode ----------------------------------------------------------------------------------------

    private DirgeId _editingSnapshotId;

    /// <summary>The snapshot whose values faders/parameters edit (FMOD-style scoping); null = authored values.</summary>
    private Snapshot? EditingSnapshot()
    {
        if (_context is null || _editingSnapshotId.IsNone) return null;
        List<Snapshot> snapshots = Model.Snapshots;
        for (int i = 0; i < snapshots.Count; i++)
        {
            if (snapshots[i].Id == _editingSnapshotId) return snapshots[i];
        }
        _editingSnapshotId = DirgeId.None; // the snapshot was removed (or undone)
        return null;
    }

    private void LeaveSnapshotEditing()
    {
        _editingSnapshotId = DirgeId.None;
        if (Selection.Kind == SelectionKind.Snapshot) Selection.Clear();
        else { _pendingScope |= ChangeScope.Snapshots | ChangeScope.Mixer; _refreshQueued = true; }
    }

    // ---- edit helpers -------------------------------------------------------------------------------------------------

    /// <summary>Write a float property: to the editing snapshot (scoping it on first touch) or to the authored model via the
    /// supplied apply/revert pair.</summary>
    private void WriteProperty(object token, string label, PropertyRef target, float value, bool continuous, Action applyAuthored, Action revertAuthored, DirgeId subject)
    {
        Snapshot? snapshot = EditingSnapshot();
        if (snapshot is not null)
        {
            float? previous = MixerGraphOps.FindValue(snapshot, target)?.Value;
            Action apply = () => MixerGraphOps.SetValue(snapshot, target, value);
            Action revert = () => MixerGraphOps.RestoreValue(snapshot, target, previous);
            if (continuous) Project.MutateContinuous(token, label + " (snapshot)", ChangeScope.Snapshots, apply, revert, snapshot.Id);
            else Project.Mutate(label + " (snapshot)", ChangeScope.Snapshots, apply, revert, snapshot.Id);
            return;
        }

        if (continuous) Project.MutateContinuous(token, label, ChangeScope.Mixer, applyAuthored, revertAuthored, subject);
        else Project.Mutate(label, ChangeScope.Mixer, applyAuthored, revertAuthored, subject);
    }

    private void UnscopeProperty(Snapshot snapshot, PropertyRef target)
    {
        SnapshotValue? existing = MixerGraphOps.FindValue(snapshot, target);
        if (existing is null) return;
        float value = existing.Value;
        Project.Mutate("Unscope snapshot value", ChangeScope.Snapshots,
            () => MixerGraphOps.Unscope(snapshot, target),
            () => MixerGraphOps.SetValue(snapshot, target, value), snapshot.Id);
    }

    private void EndContinuous() => Project.Undo.EndContinuous();

    private static float ClampDb(float value) => Math.Clamp(value, FaderMinDb, FaderMaxDb);
}
