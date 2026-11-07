using Dirge.Project;
using Dirge.Serialization;
using NUnit.Framework;

namespace Dirge.Tests.Serialization;

[TestFixture]
public sealed class ModulatorJsonTests
{
    [Test]
    public void Autopitch_RoundTrips_WithDiscriminator()
    {
        var b = new ProjectBuilder("Autopitch");
        AudioEvent ev = b.AddEvent("Vehicle/Engine");
        ParameterDefinition rpm = b.AddLocalParameter(ev, "Rpm", 0f, 4000f, 1000f);
        ClipInstrument clip = b.AddClip(b.AddTrack(ev), "res://engine.wav");
        b.AddModulator(ev, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 1000f, PitchAtMinimum = 0.5f, PitchAtMaximum = 2f }, PropertyRef.EventPitch(ev.Id), 1f);
        b.AddModulator(clip, new AutopitchModulator { Parameter = rpm.Id, ReferenceValue = 800f, PitchAtMinimum = 0.75f, PitchAtMaximum = 1.5f }, PropertyRef.InstrumentPitch(clip.Id), 0.5f);

        string json = ProjectJson.Serialize(b.Build());
        Assert.That(json, Does.Contain("\"$type\": \"Autopitch\""));

        AudioProject copy = ProjectJson.Deserialize(json);
        AudioEvent ev2 = copy.Events[0];
        var eventMod = ev2.Modulators[0] as AutopitchModulator;
        Assert.That(eventMod, Is.Not.Null);
        Assert.That(eventMod!.Parameter, Is.EqualTo(rpm.Id));
        Assert.That(eventMod.ReferenceValue, Is.EqualTo(1000f));
        Assert.That(eventMod.PitchAtMinimum, Is.EqualTo(0.5f));
        Assert.That(eventMod.PitchAtMaximum, Is.EqualTo(2f));
        Assert.That(eventMod.Amount, Is.EqualTo(1f));
        Assert.That(eventMod.Target, Is.EqualTo(PropertyRef.EventPitch(ev.Id)));

        var clipMod = ev2.Tracks[0].Instruments[0].Modulators[0] as AutopitchModulator;
        Assert.That(clipMod, Is.Not.Null);
        Assert.That(clipMod!.ReferenceValue, Is.EqualTo(800f));
        Assert.That(clipMod.PitchAtMinimum, Is.EqualTo(0.75f));
        Assert.That(clipMod.PitchAtMaximum, Is.EqualTo(1.5f));
        Assert.That(clipMod.Amount, Is.EqualTo(0.5f));

        Assert.That(ProjectJson.Serialize(copy), Is.EqualTo(json), "stable across round-trips");
    }

    [Test]
    public void MixerModulators_RoundTrip()
    {
        var b = new ProjectBuilder("Tremolo");
        Bus music = b.AddBus("Music");
        BusEffect lowpass = b.AddEffect(music, "AudioEffectLowPassFilter", ("cutoff_hz", 2000f));
        b.Project.MixerModulators.Add(new LfoModulator { Id = b.NextId(), Target = PropertyRef.BusVolume(music.Id), Amount = 3f, RateHz = 4f, Shape = LfoShape.Triangle, Phase = 0.25f });
        b.Project.MixerModulators.Add(new RandomModulator { Id = b.NextId(), Target = PropertyRef.Effect(music.Id, 0, "cutoff_hz"), Amount = 250f });

        string json = ProjectJson.Serialize(b.Build());
        Assert.That(json, Does.Contain("\"mixerModulators\""));

        AudioProject copy = ProjectJson.Deserialize(json);
        Assert.That(copy.MixerModulators, Has.Count.EqualTo(2));
        var lfo = copy.MixerModulators[0] as LfoModulator;
        Assert.That(lfo, Is.Not.Null);
        Assert.That(lfo!.Target, Is.EqualTo(PropertyRef.BusVolume(music.Id)));
        Assert.That(lfo.Amount, Is.EqualTo(3f));
        Assert.That(lfo.RateHz, Is.EqualTo(4f));
        Assert.That(lfo.Shape, Is.EqualTo(LfoShape.Triangle));
        Assert.That(lfo.Phase, Is.EqualTo(0.25f));
        var random = copy.MixerModulators[1] as RandomModulator;
        Assert.That(random, Is.Not.Null);
        Assert.That(random!.Target, Is.EqualTo(PropertyRef.Effect(music.Id, 0, "cutoff_hz")));
        Assert.That(random.Amount, Is.EqualTo(250f));
        Assert.That(lowpass.Parameters["cutoff_hz"], Is.EqualTo(2000f));

        Assert.That(ProjectJson.Serialize(copy), Is.EqualTo(json));
    }

    [Test]
    public void MissingMixerModulators_LoadsAsEmpty_FromOlderDocuments()
    {
        AudioProject copy = ProjectJson.Deserialize("{\"name\":\"Old\"}");
        Assert.That(copy.Name, Is.EqualTo("Old"));
        Assert.That(copy.MixerModulators, Is.Empty);
    }
}
