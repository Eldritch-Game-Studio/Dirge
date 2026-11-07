using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Runtime;

[TestFixture]
public sealed class AutopitchModulatorTests
{
    private const float Tolerance = 1e-5f;

    private static ProjectBuilder EngineProject(out AudioEvent ev, out ClipInstrument clip, out ParameterDefinition rpm)
    {
        var b = new ProjectBuilder();
        ev = b.AddEvent("Vehicle/Engine");
        rpm = b.AddLocalParameter(ev, "Rpm", 0f, 4000f, 1000f);
        clip = b.AddClip(b.AddTrack(ev), "res://engine.wav");
        clip.Loop = true;
        b.Project.AssetLengths["res://engine.wav"] = 2.0;
        return b;
    }

    [Test]
    public void EventAutopitch_FollowsParameter_OneAtReference()
    {
        ProjectBuilder b = EngineProject(out AudioEvent ev, out _, out ParameterDefinition rpm);
        b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f }, PropertyRef.EventPitch(ev.Id), 1f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Vehicle/Engine");
        instance.Start();
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(1f).Within(Tolerance), "default = reference");

        instance.SetParameter("Rpm", 0f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(0.5f).Within(Tolerance), "minimum");

        instance.SetParameter("Rpm", 4000f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(2f).Within(Tolerance), "maximum");

        instance.SetParameter("Rpm", 2500f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(1.5f).Within(Tolerance), "half-way between reference and maximum");

        // The voice follows the event pitch.
        List<VoiceSetPitch> pitches = h.All<VoiceSetPitch>();
        Assert.That(pitches, Is.Not.Empty);
        Assert.That(pitches[^1].Pitch, Is.EqualTo(1.5f).Within(Tolerance));
    }

    [Test]
    public void EventAutopitch_AmountScalesDeviation()
    {
        ProjectBuilder b = EngineProject(out AudioEvent ev, out _, out ParameterDefinition rpm);
        b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f }, PropertyRef.EventPitch(ev.Id), 0.5f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Vehicle/Engine");
        instance.Start();
        instance.SetParameter("Rpm", 4000f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(1.5f).Within(Tolerance));
    }

    [Test]
    public void InstrumentAutopitch_AppliesToVoiceAtStart()
    {
        ProjectBuilder b = EngineProject(out _, out ClipInstrument clip, out ParameterDefinition rpm);
        b.AddModulator(clip, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f }, PropertyRef.InstrumentPitch(clip.Id), 1f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Vehicle/Engine");
        instance.SetParameter("Rpm", 4000f, immediate: true);
        instance.Start();
        h.Step(0.1);
        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(1));
        Assert.That(plays[0].Pitch, Is.EqualTo(2f).Within(Tolerance));
        Assert.That(instance.EventPitch, Is.EqualTo(1f).Within(Tolerance), "instrument autopitch does not touch the event chain");
    }

    [Test]
    public void Autopitch_OnVolumeTarget_IsIgnored()
    {
        ProjectBuilder b = EngineProject(out AudioEvent ev, out _, out ParameterDefinition rpm);
        b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f }, PropertyRef.EventVolume(ev.Id), 1f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Vehicle/Engine");
        instance.Start();
        instance.SetParameter("Rpm", 4000f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventVolumeDb, Is.EqualTo(0f).Within(Tolerance));
        Assert.That(instance.EventPitch, Is.EqualTo(1f).Within(Tolerance));
    }

    [Test]
    public void Autopitch_WithGlobalParameter_UsesGlobalValue()
    {
        var b = new ProjectBuilder();
        ParameterDefinition speed = b.AddGlobalParameter("Speed", 0f, 10f, 5f);
        AudioEvent ev = b.AddEvent("Vehicle/Wind");
        b.AddClip(b.AddTrack(ev), "res://wind.wav");
        b.Project.AssetLengths["res://wind.wav"] = 5.0;
        b.AddModulator(ev, new AutopitchModulator { Parameter = speed.Id, ReferenceValue = 5f, PitchAtMinimum = 0.8f, PitchAtMaximum = 1.2f }, PropertyRef.EventPitch(ev.Id), 1f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Vehicle/Wind");
        instance.Start();
        h.System.SetGlobalParameter("Speed", 10f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.EventPitch, Is.EqualTo(1.2f).Within(Tolerance));
    }
}
