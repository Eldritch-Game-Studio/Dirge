using System.Collections.Generic;
using System.Linq;
using Dirge.Project;
using Dirge.Tests.Serialization;
using Dirge.Validation;
using NUnit.Framework;

namespace Dirge.Tests.Validation;

[TestFixture]
public sealed class ModelExtValidatorTests
{
    private static IEnumerable<string> Codes(AudioProject project) => ProjectValidator.Validate(project).Select(d => d.Code);

    [Test]
    public void ExtendedProject_IsValid()
    {
        Assert.That(ProjectValidator.Validate(ModelExtJsonTests.ExtendedProject()), Is.Empty);
    }

    [Test]
    public void MagnetRegion_RequiresPositiveLength()
    {
        var b = ProjectBuilder.Minimal();
        MagnetRegion magnet = b.AddMarker(b.Project.Events[0], new MagnetRegion { Length = 0f }, 1f);
        IReadOnlyList<Diagnostic> d = ProjectValidator.Validate(b.Build());
        Assert.That(d.Select(x => x.Code), Is.EqualTo(new[] { DiagnosticCodes.RegionLengthInvalid }));
        Assert.That(d[0].Subject, Is.EqualTo(magnet.Id));
    }

    [Test]
    public void AudioTable_EmptyLocales_IsError_AndEmptyAssetWarns()
    {
        var b = ProjectBuilder.Minimal();
        var table = new AudioTable { Id = b.NextId(), Name = "T" };
        table.Entries["nothing"] = new Dictionary<string, AssetRef>();
        table.Entries["blank"] = new Dictionary<string, AssetRef> { ["en"] = default };
        b.Project.AudioTables.Add(table);
        IReadOnlyList<Diagnostic> d = ProjectValidator.Validate(b.Build());
        Assert.That(d.Select(x => (x.Code, x.Severity)), Is.EquivalentTo(new[]
        {
            (DiagnosticCodes.AudioTableEmptyLocales, Severity.Error),
            (DiagnosticCodes.EmptyAssetRef, Severity.Warning),
        }));
        Assert.That(d.All(x => x.Subject == table.Id));
    }

    [Test]
    public void AudioTable_DuplicateNames()
    {
        var b = ProjectBuilder.Minimal();
        b.Project.AudioTables.Add(new AudioTable { Id = b.NextId(), Name = "T" });
        b.Project.AudioTables.Add(new AudioTable { Id = b.NextId(), Name = "T" });
        Assert.That(Codes(b.Build()), Is.EqualTo(new[] { DiagnosticCodes.DuplicateAudioTableName, DiagnosticCodes.DuplicateAudioTableName }));
    }

    [Test]
    public void PlatformOverride_TargetMustExist_AndNameMustNotBeEmpty()
    {
        var b = ProjectBuilder.Minimal();
        var po = new PlatformOverride { Platform = "" };
        po.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(b.NextId()), Value = -6f });
        po.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(b.Master.Id, 0, "x"), Value = 1f });
        b.Project.PlatformOverrides.Add(po);
        Assert.That(Codes(b.Build()), Is.EqualTo(new[]
        {
            DiagnosticCodes.PlatformOverrideEmpty,
            DiagnosticCodes.PlatformOverrideTargetMissing,
            DiagnosticCodes.PlatformOverrideTargetMissing,
        }));
    }

    [Test]
    public void MixerAutomation_RequiresMixerTargetAndGlobalParameter()
    {
        var b = ProjectBuilder.Minimal();
        ParameterDefinition local = b.AddLocalParameter(b.Project.Events[0], "Local");
        var lane = new AutomationLane { Id = b.NextId(), Target = PropertyRef.EventVolume(b.Project.Events[0].Id), Parameter = local.Id };
        lane.Curve.Points.Add(new CurvePoint(1f, 0f));
        lane.Curve.Points.Add(new CurvePoint(0f, 0f));
        b.Project.MixerAutomation.Add(lane);
        IReadOnlyList<Diagnostic> d = ProjectValidator.Validate(b.Build());
        Assert.That(d.Select(x => x.Code), Is.EqualTo(new[]
        {
            DiagnosticCodes.MixerAutomationInvalid, DiagnosticCodes.MixerAutomationInvalid, DiagnosticCodes.CurveUnsorted,
        }));
        Assert.That(d.All(x => x.Subject == lane.Id));
    }

    [Test]
    public void TransitionInstruments_NeedLength_TimelinePlacement_AndStartInsideTimeline()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        ParameterDefinition global = b.AddGlobalParameter("P");
        DestinationMarker dest = b.AddMarker(ev, new DestinationMarker(), 4f);

        TransitionMarker noLength = b.AddMarker(ev, new TransitionMarker { Destination = dest.Id }, 1f, "NoLength");
        noLength.TransitionInstruments.Add(new SilenceInstrument { Id = b.NextId(), Name = "S", Placement = new Placement { Length = 0.5f } });

        TransitionMarker bad = b.AddMarker(ev, new TransitionMarker { Destination = dest.Id, TransitionLength = 1f }, 2f, "Bad");
        var onSheet = new SilenceInstrument { Id = b.NextId(), Name = "Sheet", Placement = new Placement { Parameter = global.Id, Length = 0.5f } };
        var late = new ClipInstrument { Id = b.NextId(), Name = "Late", Asset = new AssetRef("res://x.ogg"), Placement = new Placement { Start = 1.5f } };
        var noAsset = new ClipInstrument { Id = b.NextId(), Name = "Empty", Placement = new Placement { Start = 0.2f } };
        bad.TransitionInstruments.Add(onSheet);
        bad.TransitionInstruments.Add(late);
        bad.TransitionInstruments.Add(noAsset);

        TransitionRegion negative = b.AddMarker(ev, new TransitionRegion { Destination = dest.Id, Length = 1f, TransitionLength = -1f }, 3f, "Neg");

        IReadOnlyList<Diagnostic> d = ProjectValidator.Validate(b.Build());
        Assert.That(d.Select(x => (x.Code, x.Subject)), Is.EquivalentTo(new[]
        {
            (DiagnosticCodes.TransitionInstrumentInvalid, noLength.Id),
            (DiagnosticCodes.TransitionInstrumentInvalid, onSheet.Id),
            (DiagnosticCodes.TransitionInstrumentInvalid, late.Id),
            (DiagnosticCodes.EmptyAssetRef, noAsset.Id),
            (DiagnosticCodes.TransitionInstrumentInvalid, negative.Id),
        }), string.Join("\n", d));
    }
}
