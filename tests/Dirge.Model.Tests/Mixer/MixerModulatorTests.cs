using System;
using System.Collections.Generic;
using Dirge.Evaluation;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Model.Tests.Mixer;

[TestFixture]
public sealed class MixerModulatorTests
{
    private const float Tolerance = 1e-4f;

    private AudioProject _project = null!;
    private Bus _music = null!;
    private BusEffect _lowpass = null!;
    private CommandBuffer _commands = null!;

    [SetUp]
    public void SetUp()
    {
        _project = new AudioProject { Name = "Tremolo" };
        var master = new Bus { Name = "Master" };
        _music = new Bus { Name = "Music", Parent = master.Id, VolumeDb = -6f };
        _lowpass = new BusEffect { TypeName = "AudioEffectLowPassFilter" };
        _lowpass.Parameters["cutoff_hz"] = 2000f;
        _music.Effects.Add(_lowpass);
        _project.Mixer.Buses.Add(master);
        _project.Mixer.Buses.Add(_music);
    }

    private MixerState Build(int seed)
    {
        _commands = new CommandBuffer();
        return new MixerState(_project, new ParameterBank(_project.GlobalParameters), _commands, new DirgeRandom(seed));
    }

    private float LastVolume(string bus)
    {
        float? last = null;
        foreach (Command c in _commands.Commands) if (c is BusSetVolume v && v.Bus == bus) last = v.VolumeDb;
        return last ?? throw new AssertionException($"No BusSetVolume for {bus}");
    }

    private float LastEffect(string bus, string parameter)
    {
        float? last = null;
        foreach (Command c in _commands.Commands) if (c is EffectSetParameter e && e.Bus == bus && e.Parameter == parameter) last = e.Value;
        return last ?? throw new AssertionException($"No EffectSetParameter for {bus}/{parameter}");
    }

    [Test]
    public void LfoOnBusVolume_EmitsOscillatingVolume_AtAuthoredRate()
    {
        _project.MixerModulators.Add(new LfoModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = 6f, RateHz = 1f, Shape = LfoShape.Sine });
        MixerState mixer = Build(1);

        mixer.Update(0);
        Assert.That(LastVolume("Music"), Is.EqualTo(-6f).Within(Tolerance), "phase 0");
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(0f).Within(Tolerance), "quarter period: +Amount");
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(-6f).Within(Tolerance), "half period");
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(-12f).Within(Tolerance), "three quarters: -Amount");
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(-6f).Within(Tolerance), "full period");
        Assert.That(mixer.EffectiveBusVolumeDb(_music.Id), Is.EqualTo(-6f).Within(Tolerance));
    }

    [Test]
    public void LfoRate_ScalesPeriod()
    {
        _project.MixerModulators.Add(new LfoModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = 6f, RateHz = 4f, Shape = LfoShape.Sine });
        MixerState mixer = Build(1);
        mixer.Update(0);
        _commands.Clear();
        mixer.Update(1.0 / 16); // quarter of a 0.25 s period
        Assert.That(LastVolume("Music"), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void LfoOnEffectParameter_AddsInParameterUnits()
    {
        _project.MixerModulators.Add(new LfoModulator { Target = PropertyRef.Effect(_music.Id, 0, "cutoff_hz"), Amount = 500f, RateHz = 1f, Shape = LfoShape.Square });
        MixerState mixer = Build(1);
        mixer.Update(0);
        Assert.That(LastEffect("Music", "cutoff_hz"), Is.EqualTo(2500f).Within(Tolerance), "square high half");
        _commands.Clear();
        mixer.Update(0.5);
        Assert.That(LastEffect("Music", "cutoff_hz"), Is.EqualTo(1500f).Within(Tolerance), "square low half");
    }

    [Test]
    public void LfoOnBusPitch_MultipliesRatio()
    {
        var shifter = new BusEffect { TypeName = "AudioEffectPitchShift" };
        shifter.Parameters["pitch_scale"] = 1f;
        _music.Effects.Add(shifter);
        _project.MixerModulators.Add(new LfoModulator { Target = PropertyRef.BusPitch(_music.Id), Amount = 0.1f, RateHz = 1f, Shape = LfoShape.Sine });
        MixerState mixer = Build(1);
        mixer.Update(0);
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(mixer.EffectiveBusPitch(_music.Id), Is.EqualTo(1.1f).Within(Tolerance));
        Assert.That(LastEffect("Music", "pitch_scale"), Is.EqualTo(1.1f).Within(Tolerance));
    }

    [Test]
    public void RandomOnBusVolume_IsFixed_AndDeterministicPerSeed()
    {
        _project.MixerModulators.Add(new RandomModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = 3f });

        MixerState a = Build(11);
        a.Update(0);
        float first = LastVolume("Music");
        _commands.Clear();
        a.Update(0.5);
        Assert.That(_commands.Commands, Is.Empty, "a fixed offset emits nothing after the first tick");
        Assert.That(first, Is.InRange(-9f, -3f));

        MixerState b = Build(11);
        b.Update(0);
        Assert.That(LastVolume("Music"), Is.EqualTo(first), "same seed, same offset");

        MixerState c = Build(12);
        c.Update(0);
        Assert.That(LastVolume("Music"), Is.Not.EqualTo(first), "different seed");
    }

    [Test]
    public void ModulatorsApplyAfterAutomation_AndBeforeOverrides()
    {
        var intensity = new ParameterDefinition { Name = "Intensity", Minimum = 0f, Maximum = 1f, Default = 1f };
        _project.GlobalParameters.Add(intensity);
        var lane = new AutomationLane { Target = PropertyRef.BusVolume(_music.Id), Parameter = intensity.Id };
        lane.Curve.Points.Add(new CurvePoint(0f, 0f));
        lane.Curve.Points.Add(new CurvePoint(1f, -4f));
        _project.MixerAutomation.Add(lane);
        _project.MixerModulators.Add(new LfoModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = 2f, RateHz = 1f, Shape = LfoShape.Square });

        MixerState mixer = Build(1);
        mixer.SetBusVolume("Music", 1f);
        mixer.Update(0);
        // authored -6, automation -4, LFO square high +2, override +1
        Assert.That(LastVolume("Music"), Is.EqualTo(-7f).Within(Tolerance));
    }

    [Test]
    public void AddAndRemoveModulator_AtRuntime()
    {
        MixerState mixer = Build(1);
        mixer.Update(0);
        var lfo = new LfoModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = 6f, RateHz = 1f, Shape = LfoShape.Sine };
        mixer.AddModulator(lfo);
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(0f).Within(Tolerance));

        Assert.That(mixer.RemoveModulator(lfo.Id), Is.True);
        Assert.That(mixer.RemoveModulator(lfo.Id), Is.False);
        _commands.Clear();
        mixer.Update(0.25);
        Assert.That(LastVolume("Music"), Is.EqualTo(-6f).Within(Tolerance));
    }

    [Test]
    public void AddModulator_RejectsEventOnlyKinds_AndUnknownTargets()
    {
        MixerState mixer = Build(1);
        Assert.Throws<ArgumentException>((Action)(() => mixer.AddModulator(new AhdsrModulator { Target = PropertyRef.BusVolume(_music.Id) })));
        Assert.Throws<ArgumentException>((Action)(() => mixer.AddModulator(new AutopitchModulator { Target = PropertyRef.BusPitch(_music.Id) })));
        Assert.Throws<ArgumentException>((Action)(() => mixer.AddModulator(new LfoModulator { Target = PropertyRef.BusVolume(DirgeId.New()) })));
    }

    [Test]
    public void AuthoredEventOnlyKinds_AreSkippedAtConstruction()
    {
        _project.MixerModulators.Add(new AhdsrModulator { Target = PropertyRef.BusVolume(_music.Id), Amount = -80f });
        MixerState mixer = Build(1);
        mixer.Update(0);
        Assert.That(LastVolume("Music"), Is.EqualTo(-6f).Within(Tolerance));
    }
}
