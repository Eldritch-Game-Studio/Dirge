using System.Collections.Generic;
using System.Linq;
using Dirge.Project;
using Dirge.Validation;
using NUnit.Framework;

namespace Dirge.Tests.Validation;

[TestFixture]
public sealed class ModulatorValidatorTests
{
    private static IReadOnlyList<Diagnostic> Validate(ProjectBuilder b) => ProjectValidator.Validate(b.Build());

    private static void AssertOnly(IReadOnlyList<Diagnostic> diagnostics, string code, DirgeId subject)
    {
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { code }), string.Join("\n", diagnostics));
        Assert.That(diagnostics[0].Subject, Is.EqualTo(subject));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(Severity.Error));
    }

    private static ProjectBuilder Engine(out AudioEvent ev, out ClipInstrument clip, out ParameterDefinition rpm)
    {
        var b = new ProjectBuilder();
        ev = b.AddEvent("Vehicle/Engine");
        rpm = b.AddLocalParameter(ev, "Rpm", 0f, 4000f, 1000f);
        clip = b.AddClip(b.AddTrack(ev), "res://engine.wav");
        return b;
    }

    // ---- autopitch ---------------------------------------------------------------------------------------------

    [Test]
    public void Autopitch_Valid_OnEventAndInstrument()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out ClipInstrument clip, out ParameterDefinition rpm);
        b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f }, PropertyRef.EventPitch(ev.Id), 1f);
        b.AddModulator(clip, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 4000f }, PropertyRef.InstrumentPitch(clip.Id), 1f);
        Assert.That(Validate(b), Is.Empty);
    }

    [Test]
    public void Autopitch_WithGlobalParameter_IsValid()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out _, out _);
        ParameterDefinition speed = b.AddGlobalParameter("Speed", 0f, 10f, 5f);
        b.AddModulator(ev, new AutopitchModulator { Parameter = speed.Id, ReferenceValue = 5f }, PropertyRef.EventPitch(ev.Id), 1f);
        Assert.That(Validate(b), Is.Empty);
    }

    [Test]
    public void Autopitch_UnknownParameter()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out _, out _);
        var mod = b.AddModulator(ev, new AutopitchModulator { Parameter = DirgeId.New(), ReferenceValue = 0f }, PropertyRef.EventPitch(ev.Id), 1f);
        AssertOnly(Validate(b), DiagnosticCodes.ParameterRefMissing, mod.Id);
    }

    [Test]
    public void Autopitch_ReferenceOutsideParameterRange()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out _, out ParameterDefinition rpm);
        var mod = b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 5000f }, PropertyRef.EventPitch(ev.Id), 1f);
        AssertOnly(Validate(b), DiagnosticCodes.AutopitchInvalid, mod.Id);
    }

    [TestCase(0f, 2f)]
    [TestCase(0.5f, -1f)]
    [TestCase(float.NaN, 2f)]
    public void Autopitch_NonPositivePitch(float atMinimum, float atMaximum)
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out _, out ParameterDefinition rpm);
        var mod = b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f, PitchAtMinimum = atMinimum, PitchAtMaximum = atMaximum }, PropertyRef.EventPitch(ev.Id), 1f);
        AssertOnly(Validate(b), DiagnosticCodes.AutopitchInvalid, mod.Id);
    }

    [Test]
    public void Autopitch_OnVolumeTarget()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out ClipInstrument clip, out ParameterDefinition rpm);
        var onEvent = b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f }, PropertyRef.EventVolume(ev.Id), 1f);
        var onClip = b.AddModulator(clip, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f }, PropertyRef.InstrumentVolume(clip.Id), 1f);
        IReadOnlyList<Diagnostic> diagnostics = Validate(b);
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.AutopitchInvalid, DiagnosticCodes.AutopitchInvalid }));
        Assert.That(diagnostics.Select(d => d.Subject), Is.EqualTo(new[] { onEvent.Id, onClip.Id }));
    }

    [Test]
    public void OtherEventModulators_AreNotChecked()
    {
        ProjectBuilder b = Engine(out AudioEvent ev, out ClipInstrument clip, out _);
        b.AddModulator(ev, new LfoModulator(), PropertyRef.EventVolume(ev.Id), 3f);
        b.AddModulator(clip, new RandomModulator(), PropertyRef.InstrumentPitch(clip.Id), 0.1f);
        b.AddModulator(clip, new AhdsrModulator(), PropertyRef.InstrumentVolume(clip.Id), -80f);
        Assert.That(Validate(b), Is.Empty);
    }

    // ---- mixer modulators --------------------------------------------------------------------------------------

    [Test]
    public void MixerModulators_LfoAndRandom_OnMixerProperties_AreValid()
    {
        var b = ProjectBuilder.Minimal();
        Bus music = b.AddBus("Music");
        b.AddEffect(music, "AudioEffectLowPassFilter", ("cutoff_hz", 2000f));
        Vca vca = b.AddVca("Vca", 0f, music);
        b.Project.MixerModulators.Add(new LfoModulator { Id = b.NextId(), Target = PropertyRef.BusVolume(music.Id), Amount = 3f });
        b.Project.MixerModulators.Add(new LfoModulator { Id = b.NextId(), Target = PropertyRef.BusPitch(music.Id), Amount = 0.05f });
        b.Project.MixerModulators.Add(new RandomModulator { Id = b.NextId(), Target = PropertyRef.Effect(music.Id, 0, "cutoff_hz"), Amount = 200f });
        b.Project.MixerModulators.Add(new RandomModulator { Id = b.NextId(), Target = PropertyRef.VcaVolume(vca.Id), Amount = 1f });
        Assert.That(Validate(b), Is.Empty);
    }

    [Test]
    public void MixerModulators_RejectAhdsrAndAutopitch()
    {
        var b = ProjectBuilder.Minimal();
        var ahdsr = new AhdsrModulator { Id = b.NextId(), Target = PropertyRef.BusVolume(b.Master.Id), Amount = -10f };
        var autopitch = new AutopitchModulator { Id = b.NextId(), Target = PropertyRef.BusPitch(b.Master.Id), Amount = 1f };
        b.Project.MixerModulators.Add(ahdsr);
        b.Project.MixerModulators.Add(autopitch);
        IReadOnlyList<Diagnostic> diagnostics = Validate(b);
        Assert.That(diagnostics.Select(d => d.Code), Is.EqualTo(new[] { DiagnosticCodes.MixerModulatorInvalid, DiagnosticCodes.MixerModulatorInvalid }));
        Assert.That(diagnostics.Select(d => d.Subject), Is.EqualTo(new[] { ahdsr.Id, autopitch.Id }));
    }

    [Test]
    public void MixerModulators_RejectMissingOwner_AndEventTargets()
    {
        var b = ProjectBuilder.Minimal();
        var missingBus = new LfoModulator { Id = b.NextId(), Target = PropertyRef.BusVolume(DirgeId.New()), Amount = 3f };
        var missingEffect = new RandomModulator { Id = b.NextId(), Target = PropertyRef.Effect(b.Master.Id, 3, "x"), Amount = 3f };
        var eventTarget = new LfoModulator { Id = b.NextId(), Target = PropertyRef.EventVolume(b.Project.Events[0].Id), Amount = 3f };
        b.Project.MixerModulators.Add(missingBus);
        b.Project.MixerModulators.Add(missingEffect);
        b.Project.MixerModulators.Add(eventTarget);
        IReadOnlyList<Diagnostic> diagnostics = Validate(b);
        Assert.That(diagnostics.Select(d => d.Code).Distinct(), Is.EqualTo(new[] { DiagnosticCodes.MixerModulatorInvalid }));
        Assert.That(diagnostics.Select(d => d.Subject), Is.EqualTo(new[] { missingBus.Id, missingEffect.Id, eventTarget.Id }));
    }
}
