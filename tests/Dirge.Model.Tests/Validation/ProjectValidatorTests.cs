using System.Collections.Generic;
using System.Linq;
using Dirge.Project;
using Dirge.Validation;
using NUnit.Framework;

namespace Dirge.Tests.Validation;

[TestFixture]
public sealed class ProjectValidatorTests
{
    private static IReadOnlyList<Diagnostic> Validate(ProjectBuilder b) => ProjectValidator.Validate(b.Build());

    private static void AssertOnly(IReadOnlyList<Diagnostic> diagnostics, string code, DirgeId subject, Severity severity = Severity.Error)
    {
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { code }), string.Join("\n", diagnostics));
        Assert.That(diagnostics[0].Subject, Is.EqualTo(subject));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(severity));
        Assert.That(diagnostics[0].Message, Is.Not.Empty);
    }

    [Test]
    public void ValidProject_HasNoDiagnostics()
    {
        var b = ProjectBuilder.Minimal();
        Bus music = b.AddBus("Music");
        b.AddEffect(music, "AudioEffectCompressor", ("threshold", -6f));
        Vca vca = b.AddVca("Vca", 0f, music);
        ParameterDefinition global = b.AddGlobalParameter("Intensity", 0f, 1f, 0.5f);
        ParameterDefinition labeled = b.AddGlobalParameter("Surface", 0f, 1f, 0f, ParameterKind.Labeled);
        labeled.Labels.AddRange(new[] { "A", "B" });
        Snapshot snap = b.AddSnapshot("Duck");
        snap.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(music.Id, 0, "threshold"), Value = -20f });
        snap.Values.Add(new SnapshotValue { Target = PropertyRef.VcaVolume(vca.Id), Value = -3f });

        AudioEvent ev = b.AddEvent("Music/Main", music);
        ParameterDefinition local = b.AddLocalParameter(ev, "Local", 0f, 1f);
        Track track = b.AddTrack(ev);
        ClipInstrument clip = b.AddClip(track, "res://a.ogg", 0f, 1f);
        clip.Conditions.Add(new TriggerCondition { Parameter = local.Id, Minimum = 0f, Maximum = 1f });
        var multi = b.AddSheetInstrument(track, new MultiInstrument(), global, 0f, 1f);
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://m.ogg") });
        b.AddInstrument(track, new EventInstrument { Event = b.Project.Events[0].Id }, 1f);
        b.AddInstrument(track, new SnapshotInstrument { Snapshot = snap.Id }, 1f, 1f);
        b.AddInstrument(track, new CommandInstrument { Action = CommandAction.StartEvent, TargetEvent = b.Project.Events[0].Id }, 2f);
        b.AddInstrument(track, new CommandInstrument { Action = CommandAction.SetParameter, TargetParameter = local.Id, Value = 1f }, 2f);
        b.AddInstrument(track, new CommandInstrument { Action = CommandAction.SetParameter, TargetEvent = b.Project.Events[0].Id, TargetParameter = global.Id, Value = 1f }, 2f);
        b.AddAutomation(ev, PropertyRef.EventVolume(ev.Id), global, new CurvePoint(0f, -10f), new CurvePoint(1f, 0f));
        var dest = b.AddMarker(ev, new DestinationMarker(), 4f);
        b.AddMarker(ev, new LoopRegion { Length = 2f }, 4f);
        b.AddMarker(ev, new TransitionMarker { Destination = dest.Id }, 6f);
        b.AddMarker(ev, new TransitionRegion { Destination = dest.Id, Length = 1f }, 6f);
        b.AddMarker(ev, new SustainPoint(), 7f);
        b.AddTempo(ev, 120f);

        Assert.That(Validate(b), Is.Empty);
    }

    // ---- mixer -------------------------------------------------------------------------------------------------

    [Test]
    public void MasterBusMissing()
    {
        var b = new ProjectBuilder();
        b.Project.Mixer.Buses.Clear();
        AssertOnly(Validate(b), DiagnosticCodes.MasterBusMissing, DirgeId.None);
    }

    [Test]
    public void MasterBusMultiple()
    {
        var b = new ProjectBuilder();
        Bus second = b.AddBus("Master2");
        second.Parent = DirgeId.None;
        IReadOnlyList<Diagnostic> d = Validate(b);
        Assert.That(d.Where(x => x.Code == DiagnosticCodes.MasterBusMultiple).Select(x => x.Subject), Is.EquivalentTo(new[] { b.Master.Id, second.Id }));
        Assert.That(d.Where(x => x.Code == DiagnosticCodes.MasterBusName).Select(x => x.Subject), Is.EqualTo(new[] { second.Id }));
    }

    [Test]
    public void MasterBusName()
    {
        var b = new ProjectBuilder();
        b.Master.Name = "Main";
        AssertOnly(Validate(b), DiagnosticCodes.MasterBusName, b.Master.Id);
    }

    [Test]
    public void BusParentMissing()
    {
        var b = new ProjectBuilder();
        Bus orphan = b.AddBus("Orphan");
        orphan.Parent = DirgeId.New();
        AssertOnly(Validate(b), DiagnosticCodes.BusParentMissing, orphan.Id);
    }

    [Test]
    public void BusCycle_ReportedOncePerCycle()
    {
        var b = new ProjectBuilder();
        Bus a = b.AddBus("A");
        Bus c = b.AddBus("C", a);
        a.Parent = c.Id;
        AssertOnly(Validate(b), DiagnosticCodes.BusCycle, a.Id);
    }

    [Test]
    public void DuplicateBusName()
    {
        var b = new ProjectBuilder();
        Bus x = b.AddBus("Music");
        Bus y = b.AddBus("Music");
        IReadOnlyList<Diagnostic> d = Validate(b);
        Assert.That(d.Select(x => x.Code), Is.All.EqualTo(DiagnosticCodes.DuplicateBusName));
        Assert.That(d.Select(x => x.Subject), Is.EquivalentTo(new[] { x.Id, y.Id }));
    }

    [Test]
    public void DuplicateVcaName()
    {
        var b = new ProjectBuilder();
        b.AddVca("V");
        Vca second = b.AddVca("V");
        Assert.That(Validate(b).Select(x => (x.Code, x.Subject)), Does.Contain((DiagnosticCodes.DuplicateVcaName, second.Id)));
    }

    [Test]
    public void VcaRefMissing()
    {
        var b = new ProjectBuilder();
        b.Master.Vcas.Add(DirgeId.New());
        AssertOnly(Validate(b), DiagnosticCodes.VcaRefMissing, b.Master.Id);
    }

    [Test]
    public void EffectTypeNameEmpty()
    {
        var b = new ProjectBuilder();
        BusEffect effect = b.AddEffect(b.Master, "");
        AssertOnly(Validate(b), DiagnosticCodes.EffectTypeNameEmpty, effect.Id);
    }

    [Test]
    public void BusFaderIndexNegative()
    {
        var b = new ProjectBuilder();
        Bus music = b.AddBus("Music");
        music.FaderIndex = -1;
        AssertOnly(Validate(b), DiagnosticCodes.BusFaderIndexNegative, music.Id);
    }

    [Test]
    public void BusFaderIndex_ZeroAndPastChainAreValid()
    {
        var b = new ProjectBuilder();
        Bus music = b.AddBus("Music");
        b.AddEffect(music, "AudioEffectLowPassFilter");
        music.FaderIndex = 0;
        Bus sfx = b.AddBus("SFX");
        sfx.FaderIndex = 7;
        Assert.That(Validate(b), Is.Empty);
    }

    // ---- parameters --------------------------------------------------------------------------------------------

    [Test]
    public void DuplicateParameterName_Global()
    {
        var b = new ProjectBuilder();
        b.AddGlobalParameter("P");
        ParameterDefinition second = b.AddGlobalParameter("P");
        Assert.That(Validate(b).Select(x => (x.Code, x.Subject)), Does.Contain((DiagnosticCodes.DuplicateParameterName, second.Id)));
    }

    [Test]
    public void DuplicateParameterName_Local()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        b.AddLocalParameter(ev, "P");
        ParameterDefinition second = b.AddLocalParameter(ev, "P");
        Assert.That(Validate(b).Select(x => (x.Code, x.Subject)), Does.Contain((DiagnosticCodes.DuplicateParameterName, second.Id)));
    }

    [Test]
    public void LabeledParameterNoLabels()
    {
        var b = new ProjectBuilder();
        ParameterDefinition p = b.AddGlobalParameter("Surface", kind: ParameterKind.Labeled);
        AssertOnly(Validate(b), DiagnosticCodes.LabeledParameterNoLabels, p.Id);
    }

    [Test]
    public void ParameterRangeInvalid_DefaultOutsideRange()
    {
        var b = new ProjectBuilder();
        ParameterDefinition p = b.AddGlobalParameter("P", 0f, 1f, 2f);
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRangeInvalid, p.Id);
    }

    [Test]
    public void ParameterRangeInvalid_MinAboveMax_Local()
    {
        var b = ProjectBuilder.Minimal();
        ParameterDefinition p = b.AddLocalParameter(b.Project.Events[0], "P", 5f, 1f, 3f);
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRangeInvalid, p.Id);
    }

    // ---- events ------------------------------------------------------------------------------------------------

    [Test]
    public void DuplicateEventPath()
    {
        var b = new ProjectBuilder();
        AudioEvent x = b.AddEvent("A/B");
        AudioEvent y = b.AddEvent("A/B");
        Assert.That(Validate(b).Where(d => d.Code == DiagnosticCodes.DuplicateEventPath).Select(d => d.Subject), Is.EquivalentTo(new[] { x.Id, y.Id }));
    }

    [Test]
    public void EventBusMissing()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("A");
        ev.Bus = DirgeId.New();
        AssertOnly(Validate(b), DiagnosticCodes.EventBusMissing, ev.Id);
    }

    [Test]
    public void ParameterRefMissing_InstrumentPlacement()
    {
        var b = ProjectBuilder.Minimal();
        Instrument clip = b.Project.Events[0].Tracks[0].Instruments[0];
        clip.Placement.Parameter = DirgeId.New();
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, clip.Id);
    }

    [Test]
    public void ParameterRefMissing_InstrumentCondition()
    {
        var b = ProjectBuilder.Minimal();
        Instrument clip = b.Project.Events[0].Tracks[0].Instruments[0];
        clip.Conditions.Add(new TriggerCondition { Parameter = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, clip.Id);
    }

    [Test]
    public void ParameterRefMissing_LocalOfOtherEventDoesNotResolve()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent other = b.AddEvent("Other");
        ParameterDefinition foreign = b.AddLocalParameter(other, "Foreign");
        Instrument clip = b.Project.Events[0].Tracks[0].Instruments[0];
        clip.Placement.Parameter = foreign.Id;
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, clip.Id);
    }

    [Test]
    public void ParameterRefMissing_MarkerCondition()
    {
        var b = ProjectBuilder.Minimal();
        var marker = b.AddMarker(b.Project.Events[0], new SustainPoint(), 1f);
        marker.Conditions.Add(new TriggerCondition { Parameter = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, marker.Id);
    }

    [Test]
    public void ParameterRefMissing_Automation()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        var lane = b.AddAutomation(ev, PropertyRef.EventVolume(ev.Id), new ParameterDefinition(), new CurvePoint(0f, 0f));
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, lane.Id);
    }

    [Test]
    public void CurveUnsorted()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        ParameterDefinition p = b.AddGlobalParameter("P");
        var lane = b.AddAutomation(ev, PropertyRef.EventVolume(ev.Id), p, new CurvePoint(0f, 0f), new CurvePoint(1f, 1f), new CurvePoint(0.5f, 0.5f));
        AssertOnly(Validate(b), DiagnosticCodes.CurveUnsorted, lane.Id);
    }

    [Test]
    public void EventInstrumentTargetMissing()
    {
        var b = ProjectBuilder.Minimal();
        var nested = b.AddInstrument(b.Project.Events[0].Tracks[0], new EventInstrument { Event = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.EventInstrumentTargetMissing, nested.Id);
    }

    [Test]
    public void EventInstrumentCycle_Direct()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        var self = b.AddInstrument(ev.Tracks[0], new EventInstrument { Event = ev.Id });
        AssertOnly(Validate(b), DiagnosticCodes.EventInstrumentCycle, self.Id);
    }

    [Test]
    public void EventInstrumentCycle_Transitive_ReportedOnce()
    {
        var b = new ProjectBuilder();
        AudioEvent a = b.AddEvent("A");
        AudioEvent c = b.AddEvent("B");
        AudioEvent d = b.AddEvent("C");
        b.AddInstrument(b.AddTrack(a), new EventInstrument { Event = c.Id });
        b.AddInstrument(b.AddTrack(c), new EventInstrument { Event = d.Id });
        var back = b.AddInstrument(b.AddTrack(d), new EventInstrument { Event = a.Id });
        AssertOnly(Validate(b), DiagnosticCodes.EventInstrumentCycle, back.Id);
    }

    [Test]
    public void EventInstrument_Diamond_IsNotACycle()
    {
        var b = new ProjectBuilder();
        AudioEvent root = b.AddEvent("Root");
        AudioEvent left = b.AddEvent("Left");
        AudioEvent right = b.AddEvent("Right");
        AudioEvent leaf = b.AddEvent("Leaf");
        Track t = b.AddTrack(root);
        b.AddInstrument(t, new EventInstrument { Event = left.Id });
        b.AddInstrument(t, new EventInstrument { Event = right.Id });
        b.AddInstrument(b.AddTrack(left), new EventInstrument { Event = leaf.Id });
        b.AddInstrument(b.AddTrack(right), new EventInstrument { Event = leaf.Id });
        Assert.That(Validate(b), Is.Empty);
    }

    [Test]
    public void SnapshotTargetMissing_Instrument()
    {
        var b = ProjectBuilder.Minimal();
        var instrument = b.AddInstrument(b.Project.Events[0].Tracks[0], new SnapshotInstrument { Snapshot = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.SnapshotTargetMissing, instrument.Id);
    }

    [Test]
    public void SnapshotTargetMissing_ValueOwner()
    {
        var b = new ProjectBuilder();
        Snapshot snap = b.AddSnapshot("S");
        snap.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(DirgeId.New()) });
        AssertOnly(Validate(b), DiagnosticCodes.SnapshotTargetMissing, snap.Id);
    }

    [Test]
    public void SnapshotTargetMissing_EffectIndexOutOfRange()
    {
        var b = new ProjectBuilder();
        b.AddEffect(b.Master, "AudioEffectLowPassFilter");
        Snapshot snap = b.AddSnapshot("S");
        snap.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(b.Master.Id, 1, "cutoff_hz") });
        AssertOnly(Validate(b), DiagnosticCodes.SnapshotTargetMissing, snap.Id);
    }

    [Test]
    public void CommandTargetMissing_Event()
    {
        var b = ProjectBuilder.Minimal();
        var command = b.AddInstrument(b.Project.Events[0].Tracks[0], new CommandInstrument { Action = CommandAction.StopEvent, TargetEvent = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.CommandTargetMissing, command.Id);
    }

    [Test]
    public void CommandTargetMissing_Parameter()
    {
        var b = ProjectBuilder.Minimal();
        var command = b.AddInstrument(b.Project.Events[0].Tracks[0], new CommandInstrument { Action = CommandAction.SetParameter, TargetParameter = DirgeId.New() });
        AssertOnly(Validate(b), DiagnosticCodes.CommandTargetMissing, command.Id);
    }

    [Test]
    public void CommandTargetMissing_ParameterOfOtherEvent()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent other = b.AddEvent("Other");
        ParameterDefinition ownLocal = b.AddLocalParameter(b.Project.Events[0], "Own");
        var command = b.AddInstrument(b.Project.Events[0].Tracks[0], new CommandInstrument { Action = CommandAction.SetParameter, TargetEvent = other.Id, TargetParameter = ownLocal.Id });
        AssertOnly(Validate(b), DiagnosticCodes.CommandTargetMissing, command.Id);
    }

    [Test]
    public void EmptyAssetRef_Clip_IsWarning()
    {
        var b = ProjectBuilder.Minimal();
        var clip = (ClipInstrument)b.Project.Events[0].Tracks[0].Instruments[0];
        clip.Asset = default;
        AssertOnly(Validate(b), DiagnosticCodes.EmptyAssetRef, clip.Id, Severity.Warning);
    }

    [Test]
    public void EmptyAssetRef_MultiEntry_IsWarning()
    {
        var b = ProjectBuilder.Minimal();
        var multi = b.AddInstrument(b.Project.Events[0].Tracks[0], new MultiInstrument());
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://ok.ogg") });
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("") });
        AssertOnly(Validate(b), DiagnosticCodes.EmptyAssetRef, multi.Id, Severity.Warning);
    }

    [Test]
    public void MultiInstrumentEmpty()
    {
        var b = ProjectBuilder.Minimal();
        var multi = b.AddInstrument(b.Project.Events[0].Tracks[0], new MultiInstrument());
        AssertOnly(Validate(b), DiagnosticCodes.MultiInstrumentEmpty, multi.Id);
    }

    [Test]
    public void MultiInstrumentEmpty_Scatterer()
    {
        var b = ProjectBuilder.Minimal();
        var scatterer = b.AddInstrument(b.Project.Events[0].Tracks[0], new ScattererInstrument());
        AssertOnly(Validate(b), DiagnosticCodes.MultiInstrumentEmpty, scatterer.Id);
    }

    [Test]
    public void RegionLengthInvalid_Loop()
    {
        var b = ProjectBuilder.Minimal();
        var loop = b.AddMarker(b.Project.Events[0], new LoopRegion { Length = 0f }, 1f);
        AssertOnly(Validate(b), DiagnosticCodes.RegionLengthInvalid, loop.Id);
    }

    [Test]
    public void RegionLengthInvalid_Transition()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        var dest = b.AddMarker(ev, new DestinationMarker(), 0f);
        var region = b.AddMarker(ev, new TransitionRegion { Destination = dest.Id, Length = -1f }, 1f);
        AssertOnly(Validate(b), DiagnosticCodes.RegionLengthInvalid, region.Id);
    }

    [Test]
    public void TransitionDestinationInvalid_Missing()
    {
        var b = ProjectBuilder.Minimal();
        var transition = b.AddMarker(b.Project.Events[0], new TransitionMarker { Destination = DirgeId.New() }, 1f);
        AssertOnly(Validate(b), DiagnosticCodes.TransitionDestinationInvalid, transition.Id);
    }

    [Test]
    public void TransitionDestinationInvalid_NotADestinationMarker()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        var sustain = b.AddMarker(ev, new SustainPoint(), 0f);
        var transition = b.AddMarker(ev, new TransitionMarker { Destination = sustain.Id }, 1f);
        AssertOnly(Validate(b), DiagnosticCodes.TransitionDestinationInvalid, transition.Id);
    }

    [Test]
    public void TransitionDestinationInvalid_OtherEvent()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent other = b.AddEvent("Other");
        var dest = b.AddMarker(other, new DestinationMarker(), 0f);
        var region = b.AddMarker(b.Project.Events[0], new TransitionRegion { Destination = dest.Id, Length = 1f }, 1f);
        AssertOnly(Validate(b), DiagnosticCodes.TransitionDestinationInvalid, region.Id);
    }

    [Test]
    public void TempoBpmInvalid()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        b.AddTempo(ev, 0f);
        AssertOnly(Validate(b), DiagnosticCodes.TempoBpmInvalid, ev.Id);
    }
}
