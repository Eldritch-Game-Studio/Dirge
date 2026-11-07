#nullable enable
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using NecronomiDom.Dom;

namespace Dirge.Tool.Mixer;

public sealed partial class MixerPane
{
    private const float LfoRateMinHz = 0.05f;
    private const float LfoRateMaxHz = 20f;
    private const float LfoDepthMaxDb = 24f;
    private const float LfoDefaultRateHz = 4f;
    private const float LfoDefaultDepthDb = 3f;

    /// <summary>One bus-volume LFO in the popover; kept so refreshes update values in place.</summary>
    private sealed class LfoRow
    {
        public DirgeId Id { get; }
        public HTMLElement Shape { get; }
        public HTMLElement Remove { get; }
        public Slider Rate { get; }
        public HTMLInputElement RateInput { get; }
        public Slider Depth { get; }
        public HTMLInputElement DepthInput { get; }
        public object RateToken { get; } = new();
        public object DepthToken { get; } = new();

        public LfoRow(Dom dom, HTMLElement list, LfoModulator lfo, int index)
        {
            Id = lfo.Id;
            HTMLElement root = dom.El(list, "div", "lfo-row");
            root.Dataset["lfo"] = lfo.Id.ToString();

            HTMLElement head = dom.El(root, "div", "lfo-row-head");
            dom.El(head, "div", "field-label", "LFO " + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Shape = dom.El(head, "button", "pill", "");
            Shape.Title = "Waveform";
            Remove = dom.El(head, "button", "fx-x", "x");
            Remove.Title = "Remove this LFO";

            HTMLElement rateRow = dom.El(root, "div", "param-row");
            dom.El(rateRow, "div", "field-label", "Rate");
            Rate = HSlider(dom, rateRow, LfoRateMinHz, LfoRateMaxHz, step: 0.05f, reset: LfoDefaultRateHz);
            RateInput = dom.Input(rateRow, "num", "");
            dom.El(rateRow, "div", "field-label", "Hz");

            HTMLElement depthRow = dom.El(root, "div", "param-row");
            dom.El(depthRow, "div", "field-label", "Depth");
            Depth = HSlider(dom, depthRow, 0f, LfoDepthMaxDb, step: 0.1f, reset: LfoDefaultDepthDb);
            DepthInput = dom.Input(depthRow, "num", "");
            dom.El(depthRow, "div", "field-label", "dB");
        }

        private static Slider HSlider(Dom dom, HTMLElement row, float min, float max, float step, float reset)
        {
            HTMLElement track = dom.El(row, "div", "hslider");
            HTMLElement fill = dom.El(track, "div", "hfill");
            HTMLElement thumb = dom.El(track, "div", "hthumb");
            return new Slider(dom, track, thumb, fill, vertical: false, min, max, HSliderLengthPx, HSliderThumbPx) { Step = step, WheelStep = step * 2f, ResetValue = reset };
        }
    }

    private HTMLElement _lfoPopover = null!;
    private HTMLElement _lfoPopoverTitle = null!;
    private HTMLElement _lfoPopoverBody = null!;
    private DirgeId _lfoBus;
    private Dom? _lfoDom;
    private readonly List<LfoRow> _lfoRows = new();
    private readonly List<LfoModulator> _lfoScratch = new();
    private static readonly string[] LfoShapeNames = Enum.GetNames<LfoShape>();

    private void BindModulators()
    {
        Dom dom = _dom!;
        _lfoPopover = dom.Required("lfo-popover");
        _lfoPopoverTitle = dom.Required("lfo-popover-title");
        _lfoPopoverBody = dom.Required("lfo-popover-body");
        dom.On(dom.Required("lfo-popover-close"), "click", CloseLfoPopover);
        dom.On(dom.Required("lfo-add"), "click", () => AddBusLfo(_lfoBus));
        dom.On(dom.Document, "pointerdown", OnDocumentPointerDownForLfoPopover);
    }

    private void UnbindModulators() => CloseLfoPopover();

    /// <summary><see cref="AudioProject.MixerModulators"/> LFOs targeting the volume of <paramref name="bus"/>, in project order.</summary>
    private static void BusVolumeLfos(AudioProject project, DirgeId bus, List<LfoModulator> into)
    {
        into.Clear();
        PropertyRef target = PropertyRef.BusVolume(bus);
        List<Modulator> modulators = project.MixerModulators;
        for (int i = 0; i < modulators.Count; i++)
            if (modulators[i] is LfoModulator lfo && lfo.Target == target) into.Add(lfo);
    }

    private LfoModulator? FindLfo(DirgeId id)
    {
        List<Modulator> modulators = Model.MixerModulators;
        for (int i = 0; i < modulators.Count; i++)
            if (modulators[i].Id == id && modulators[i] is LfoModulator lfo) return lfo;
        return null;
    }

    private void RefreshLfoButton(BusStrip strip, Bus bus)
    {
        BusVolumeLfos(Model, bus.Id, _lfoScratch);
        int count = _lfoScratch.Count;
        Dom.SetText(strip.LfoButton, count == 0 ? "LFO" : "LFO " + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Dom.SetClass(strip.LfoButton, "on", count > 0);
    }

    // ---- popover ------------------------------------------------------------------------------------------------------------

    private void OpenLfoPopover(BusStrip strip, Event e)
    {
        if (!_lfoPopover.Hidden && _lfoBus == strip.Id) { CloseLfoPopover(); return; }
        _popup.Hide();
        ClosePopover();
        _lfoBus = strip.Id;
        BuildLfoPopover();
        if (_lfoBus.IsNone) return;
        (double x, double y) = PopupAnchor(strip.LfoButton, e);
        Godot.Vector2 size = _dom!.Host.Size;
        _lfoPopover.Style.SetProperty("left", Dom.Px((int)Math.Clamp(x, 0, Math.Max(0, size.X - 302))));
        _lfoPopover.Style.SetProperty("top", Dom.Px((int)Math.Clamp(y, 0, Math.Max(0, size.Y - 200))));
        _lfoPopover.Hidden = false;
    }

    private void CloseLfoPopover()
    {
        if (_lfoPopover is null) return;
        _lfoPopover.Hidden = true;
        _lfoDom?.DetachAll();
        _lfoDom = null;
        _lfoRows.Clear();
        _lfoPopoverBody.TextContent = "";
        _lfoBus = DirgeId.None;
    }

    /// <summary>After a project change: rebuild rows when the LFO set changed, otherwise update values in place; close when the bus is gone.</summary>
    private void RefreshLfoPopover()
    {
        if (_lfoPopover is null || _lfoPopover.Hidden) return;
        if (MixerGraphOps.FindBus(Model.Mixer, _lfoBus) is null) { CloseLfoPopover(); return; }
        BusVolumeLfos(Model, _lfoBus, _lfoScratch);
        bool same = _lfoScratch.Count == _lfoRows.Count;
        for (int i = 0; same && i < _lfoRows.Count; i++) same = _lfoRows[i].Id == _lfoScratch[i].Id;
        if (!same) { BuildLfoPopover(); return; }
        for (int i = 0; i < _lfoRows.Count; i++) ApplyLfoRow(_lfoRows[i], _lfoScratch[i]);
    }

    private void BuildLfoPopover()
    {
        _lfoDom?.DetachAll();
        _lfoRows.Clear();
        _lfoPopoverBody.TextContent = "";
        Bus? bus = MixerGraphOps.FindBus(Model.Mixer, _lfoBus);
        if (bus is null) { CloseLfoPopover(); return; }
        var dom = new Dom(_dom!.Host);
        _lfoDom = dom;
        Dom.SetText(_lfoPopoverTitle, bus.Name + " / Volume LFO");
        BusVolumeLfos(Model, _lfoBus, _lfoScratch);
        if (_lfoScratch.Count == 0)
        {
            dom.El(_lfoPopoverBody, "div", "param-empty", "No LFO on this bus volume. \"+ LFO\" adds a 4 Hz sine tremolo of 3 dB depth.");
            return;
        }
        for (int i = 0; i < _lfoScratch.Count; i++)
        {
            LfoModulator lfo = _lfoScratch[i];
            var row = new LfoRow(dom, _lfoPopoverBody, lfo, i);
            _lfoRows.Add(row);
            WireLfoRow(dom, row);
            ApplyLfoRow(row, lfo);
        }
    }

    private void WireLfoRow(Dom dom, LfoRow row)
    {
        dom.On(row.Shape, "click", e =>
        {
            LfoModulator? lfo = FindLfo(row.Id);
            if (lfo is null) return;
            _popupItems.Clear();
            for (int i = 0; i < LfoShapeNames.Length; i++)
            {
                var shape = (LfoShape)i;
                _popupItems.Add(new Popup.Item(LfoShapeNames[i], () => WriteLfoShape(row.Id, shape), current: lfo.Shape == shape));
            }
            (double x, double y) = PopupAnchor(row.Shape, e);
            _popup.Show("WAVEFORM", _popupItems, x, y);
        });
        dom.On(row.Remove, "click", () => RemoveBusLfo(row.Id));

        row.Rate.ValueChanged += (value, continuous) => WriteLfoRate(row, value, continuous);
        row.Rate.DragEnded += EndContinuous;
        WireNumberInput(dom, row.RateInput, value => WriteLfoRate(row, Math.Clamp(value, LfoRateMinHz, LfoRateMaxHz), false));

        row.Depth.ValueChanged += (value, continuous) => WriteLfoDepth(row, value, continuous);
        row.Depth.DragEnded += EndContinuous;
        WireNumberInput(dom, row.DepthInput, value => WriteLfoDepth(row, Math.Clamp(value, 0f, LfoDepthMaxDb), false));
    }

    private void WireNumberInput(Dom dom, HTMLInputElement input, Action<float> commit)
    {
        void Commit()
        {
            if (Dom.TryParse(input.Value, out float parsed)) commit(parsed);
            else RefreshLfoPopover();
        }
        dom.On(input, "change", Commit);
        dom.On(input, "keydown", e => { if (e is KeyboardEvent k && k.Key == "Enter") { e.PreventDefault(); Commit(); } });
    }

    private void ApplyLfoRow(LfoRow row, LfoModulator lfo)
    {
        Dom.SetText(row.Shape, lfo.Shape.ToString() + " v");
        if (!row.Rate.IsDragging) row.Rate.SetValue(lfo.RateHz);
        if (!ReferenceEquals(row.RateInput.OwnerDocument?.FocusedElement, row.RateInput))
        {
            string text = Dom.Num(lfo.RateHz, 0.01f);
            if (row.RateInput.Value != text) row.RateInput.Value = text;
        }
        if (!row.Depth.IsDragging) row.Depth.SetValue(lfo.Amount);
        if (!ReferenceEquals(row.DepthInput.OwnerDocument?.FocusedElement, row.DepthInput))
        {
            string text = Dom.Num(lfo.Amount, 0.1f);
            if (row.DepthInput.Value != text) row.DepthInput.Value = text;
        }
    }

    // ---- edits ---------------------------------------------------------------------------------------------------------------
    // Subjects are modulator ids (not a bus), so the audition rebuilds instead of attempting a value-only live push.

    private void AddBusLfo(DirgeId busId)
    {
        AudioProject project = Model;
        if (MixerGraphOps.FindBus(project.Mixer, busId) is null) return;
        var lfo = new LfoModulator { Target = PropertyRef.BusVolume(busId), Amount = LfoDefaultDepthDb, RateHz = LfoDefaultRateHz, Shape = LfoShape.Sine };
        Project.Mutate("Add bus LFO", ChangeScope.Mixer,
            () => { if (!project.MixerModulators.Contains(lfo)) project.MixerModulators.Add(lfo); },
            () => project.MixerModulators.Remove(lfo), lfo.Id);
    }

    private void RemoveBusLfo(DirgeId id)
    {
        AudioProject project = Model;
        int index = -1;
        for (int i = 0; i < project.MixerModulators.Count; i++)
            if (project.MixerModulators[i].Id == id) { index = i; break; }
        if (index < 0) return;
        Modulator removed = project.MixerModulators[index];
        int at = index;
        Project.Mutate("Remove bus LFO", ChangeScope.Mixer,
            () => project.MixerModulators.Remove(removed),
            () => { if (!project.MixerModulators.Contains(removed)) project.MixerModulators.Insert(Math.Min(at, project.MixerModulators.Count), removed); }, id);
    }

    private void WriteLfoShape(DirgeId id, LfoShape shape)
    {
        LfoModulator? lfo = FindLfo(id);
        if (lfo is null || lfo.Shape == shape) return;
        LfoShape before = lfo.Shape;
        Project.Mutate("LFO waveform", ChangeScope.Mixer, () => lfo.Shape = shape, () => lfo.Shape = before, id);
    }

    private void WriteLfoRate(LfoRow row, float value, bool continuous)
    {
        LfoModulator? lfo = FindLfo(row.Id);
        if (lfo is null) return;
        float before = lfo.RateHz;
        if (!continuous && before == value) return;
        Action apply = () => lfo.RateHz = value;
        Action revert = () => lfo.RateHz = before;
        if (continuous) Project.MutateContinuous(row.RateToken, "LFO rate", ChangeScope.Mixer, apply, revert, lfo.Id);
        else { Project.Mutate("LFO rate", ChangeScope.Mixer, apply, revert, lfo.Id); EndContinuous(); }
    }

    private void WriteLfoDepth(LfoRow row, float value, bool continuous)
    {
        LfoModulator? lfo = FindLfo(row.Id);
        if (lfo is null) return;
        float before = lfo.Amount;
        if (!continuous && before == value) return;
        Action apply = () => lfo.Amount = value;
        Action revert = () => lfo.Amount = before;
        if (continuous) Project.MutateContinuous(row.DepthToken, "LFO depth", ChangeScope.Mixer, apply, revert, lfo.Id);
        else { Project.Mutate("LFO depth", ChangeScope.Mixer, apply, revert, lfo.Id); EndContinuous(); }
    }

    private void OnDocumentPointerDownForLfoPopover(Event e)
    {
        if (_lfoPopover is null || _lfoPopover.Hidden) return;
        for (NecronomiDom.Dom.Node? node = e.Target as NecronomiDom.Dom.Node; node is not null; node = node.ParentNode)
        {
            if (ReferenceEquals(node, _lfoPopover) || ReferenceEquals(node, _popup.Root)) return;
            // Pressing another strip's LFO button re-targets the popover instead of closing it.
            if (node is HTMLElement html && html.ClassList.Contains("fx-lfo")) return;
        }
        CloseLfoPopover();
    }
}
