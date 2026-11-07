using System;
using System.Collections.Generic;
using Dirge.Evaluation;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Model.Tests.Mixer;

[TestFixture]
public sealed class MixerStateTests
{
    private const float Tolerance = 1e-5f;

    private AudioProject _project = null!;
    private Bus _master = null!;
    private Bus _music = null!;
    private Bus _sfx = null!;
    private Vca _vca = null!;
    private ParameterDefinition _intensityParam = null!;
    private ParameterBank _globals = null!;
    private CommandBuffer _commands = null!;
    private MixerState _mixer = null!;

    [SetUp]
    public void SetUp()
    {
        _project = new AudioProject { Name = "MixerTest" };
        _master = new Bus { Name = "Master" };
        _music = new Bus { Name = "Music", Parent = _master.Id, VolumeDb = 0f };
        _sfx = new Bus { Name = "SFX", Parent = _master.Id, VolumeDb = -3f };
        _vca = new Vca { Name = "Main", VolumeDb = 0f };
        _music.Vcas.Add(_vca.Id);
        _sfx.Vcas.Add(_vca.Id);

        var lowpass = new BusEffect { TypeName = "AudioEffectLowPassFilter" };
        lowpass.Parameters["cutoff_hz"] = 2000f;
        lowpass.Parameters["resonance"] = 0.5f;
        _music.Effects.Add(lowpass);

        _project.Mixer.Buses.Add(_master);
        _project.Mixer.Buses.Add(_music);
        _project.Mixer.Buses.Add(_sfx);
        _project.Mixer.Vcas.Add(_vca);

        _intensityParam = new ParameterDefinition { Name = "Intensity", Minimum = 0f, Maximum = 1f, Default = 0f };
        _project.GlobalParameters.Add(_intensityParam);

        _globals = new ParameterBank(_project.GlobalParameters);
        _commands = new CommandBuffer();
        _mixer = new MixerState(_project, _globals, _commands, new DirgeRandom(1));
    }

    private Snapshot AddSnapshot(string name, int priority, SnapshotMode mode, params SnapshotValue[] values)
    {
        var snapshot = new Snapshot { Name = name, Priority = priority, Mode = mode };
        snapshot.Values.AddRange(values);
        _project.Snapshots.Add(snapshot);
        return snapshot;
    }

    private static SnapshotValue Target(PropertyRef target, float value) => new() { Target = target, Value = value };

    /// <summary>Snapshots are resolved at construction, so tests that author snapshots rebuild the mixer.</summary>
    private void Rebuild()
    {
        _commands = new CommandBuffer();
        _mixer = new MixerState(_project, _globals, _commands, new DirgeRandom(1));
    }

    private List<T> Emitted<T>() where T : Command
    {
        var list = new List<T>();
        foreach (Command c in _commands.Commands) if (c is T t) list.Add(t);
        return list;
    }

    private float Volume(string bus)
    {
        List<BusSetVolume> volumes = Emitted<BusSetVolume>();
        for (int i = volumes.Count - 1; i >= 0; i--) if (volumes[i].Bus == bus) return volumes[i].VolumeDb;
        throw new AssertionException($"No BusSetVolume for {bus}");
    }

    [Test]
    public void FirstUpdate_EmitsEveryBusAndEffectExactlyOnce()
    {
        _mixer.Update(0.016);

        List<BusSetVolume> volumes = Emitted<BusSetVolume>();
        List<BusSetMute> mutes = Emitted<BusSetMute>();
        List<BusSetSolo> solos = Emitted<BusSetSolo>();
        List<BusSetBypass> bypasses = Emitted<BusSetBypass>();
        List<EffectSetEnabled> enabled = Emitted<EffectSetEnabled>();
        List<EffectSetParameter> parameters = Emitted<EffectSetParameter>();

        Assert.That(volumes, Has.Count.EqualTo(3));
        Assert.That(mutes, Has.Count.EqualTo(3));
        Assert.That(solos, Has.Count.EqualTo(3));
        Assert.That(bypasses, Has.Count.EqualTo(3));
        Assert.That(enabled, Has.Count.EqualTo(1));
        Assert.That(parameters, Has.Count.EqualTo(2));
        Assert.That(_commands.Count, Is.EqualTo(3 * 4 + 1 + 2));

        var volumeBuses = new HashSet<string>();
        foreach (BusSetVolume v in volumes) volumeBuses.Add(v.Bus);
        Assert.That(volumeBuses, Is.EquivalentTo(new[] { "Master", "Music", "SFX" }));
        Assert.That(Volume("SFX"), Is.EqualTo(-3f).Within(Tolerance));

        var parameterNames = new HashSet<string>();
        foreach (EffectSetParameter p in parameters)
        {
            Assert.That(p.Bus, Is.EqualTo("Music"));
            Assert.That(p.EffectIndex, Is.EqualTo(0));
            parameterNames.Add(p.Parameter);
        }
        Assert.That(parameterNames, Is.EquivalentTo(new[] { "cutoff_hz", "resonance" }));
        Assert.That(enabled[0], Is.EqualTo(new EffectSetEnabled("Music", 0, true)));
    }

    [Test]
    public void UnchangedState_EmitsNothing()
    {
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.Update(0.016);
        _mixer.Update(0.016);

        Assert.That(_commands.Count, Is.Zero);
    }

    [Test]
    public void VcaMinusSix_LowersBothBuses()
    {
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.SetVcaVolume("Main", -6f);
        _mixer.Update(0.016);

        List<BusSetVolume> volumes = Emitted<BusSetVolume>();
        Assert.That(volumes, Has.Count.EqualTo(2), "only the two buses with the VCA change");
        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance));
        Assert.That(Volume("SFX"), Is.EqualTo(-9f).Within(Tolerance));
        Assert.That(_mixer.EffectiveBusVolumeDb(_music.Id), Is.EqualTo(-6f).Within(Tolerance));
        Assert.That(_mixer.EffectiveBusVolumeDb(_master.Id), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void BlendingSnapshot_HalfIntensity_HalvesDelta()
    {
        AddSnapshot("Duck", 0, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        SnapshotInstance instance = _mixer.StartSnapshot("Duck");
        instance.Intensity = 0.5f;
        _mixer.Update(0.016);

        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance));
        Assert.That(instance.IsActive, Is.True);
    }

    [Test]
    public void TwoSnapshots_HigherPriorityOverridingWins()
    {
        AddSnapshot("Low", 1, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -20f));
        AddSnapshot("High", 5, SnapshotMode.Overriding, Target(PropertyRef.BusVolume(_music.Id), -6f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.StartSnapshot("Low");
        _mixer.StartSnapshot("High");
        _mixer.Update(0.016);
        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance), "overriding snapshot discards the lower-priority result");

        // Start order must not matter.
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();
        _mixer.StartSnapshot("High");
        _mixer.StartSnapshot("Low");
        _mixer.Update(0.016);
        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance));
    }

    [Test]
    public void TwoSnapshots_OverridingAtHalfIntensity_LerpsFromAuthoredNotLower()
    {
        AddSnapshot("Low", 1, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -20f));
        AddSnapshot("High", 5, SnapshotMode.Overriding, Target(PropertyRef.BusVolume(_music.Id), -6f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.StartSnapshot("Low");
        _mixer.StartSnapshot("High").Intensity = 0.5f;
        _mixer.Update(0.016);

        Assert.That(Volume("Music"), Is.EqualTo(-3f).Within(Tolerance));
    }

    [Test]
    public void TwoSnapshots_BlendingComposesInPriorityOrder()
    {
        AddSnapshot("Low", 1, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        AddSnapshot("High", 2, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -6f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        // Low applied first (full): -12. High applied on top at 0.5: lerp(-12, -6, 0.5) = -9.
        _mixer.StartSnapshot("High").Intensity = 0.5f;
        _mixer.StartSnapshot("Low");
        _mixer.Update(0.016);

        Assert.That(Volume("Music"), Is.EqualTo(-9f).Within(Tolerance));
    }

    [Test]
    public void Ahdsr_AttackOneSecond_ReachesFullAtOne()
    {
        Snapshot snapshot = AddSnapshot("Fade", 0, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        snapshot.Intensity.Attack = 1f;
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.StartSnapshot("Fade");
        _mixer.Update(0.5);
        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance), "half way through the attack");

        _mixer.Update(0.5);
        Assert.That(Volume("Music"), Is.EqualTo(-12f).Within(Tolerance), "attack complete at t=1");

        _commands.Clear();
        _mixer.Update(0.5);
        Assert.That(_commands.Count, Is.Zero, "sustain holds; nothing new to emit");
    }

    [Test]
    public void Stop_Releases_RemovesInstance_ValueReturnsToAuthored()
    {
        Snapshot snapshot = AddSnapshot("Duck", 0, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        snapshot.Intensity.Release = 1f;
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        SnapshotInstance instance = _mixer.StartSnapshot("Duck");
        _mixer.Update(0.016);
        Assert.That(Volume("Music"), Is.EqualTo(-12f).Within(Tolerance));

        instance.Stop();
        _mixer.Update(0.5);
        Assert.That(instance.IsActive, Is.True, "still releasing");
        Assert.That(Volume("Music"), Is.EqualTo(-6f).Within(Tolerance), "half way through release");

        _mixer.Update(0.5);
        Assert.That(instance.IsActive, Is.False, "removed once the release finished");
        Assert.That(_mixer.ActiveSnapshotCount, Is.Zero);
        Assert.That(Volume("Music"), Is.EqualTo(0f).Within(Tolerance), "back to authored");
        Assert.That(_mixer.EffectiveBusVolumeDb(_music.Id), Is.EqualTo(0f).Within(Tolerance));

        // Zero-length release: removed on the next update.
        SnapshotInstance again = _mixer.StartSnapshot("Duck");
        again.Snapshot.Intensity.Release = 0f;
        _mixer.Update(0.016);
        again.Stop();
        _mixer.Update(0.016);
        Assert.That(again.IsActive, Is.False);
        Assert.That(Volume("Music"), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void SetBusVolume_AddsOnTopOfSnapshot()
    {
        AddSnapshot("Duck", 0, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.StartSnapshot("Duck");
        _mixer.SetBusVolume("Music", -4f);
        _mixer.Update(0.016);

        Assert.That(Volume("Music"), Is.EqualTo(-16f).Within(Tolerance));
        Assert.That(_mixer.EffectiveBusVolumeDb(_music.Id), Is.EqualTo(-16f).Within(Tolerance));
    }

    [Test]
    public void EffectParameterChange_EmitsOneEffectSetParameter()
    {
        AddSnapshot("Muffle", 0, SnapshotMode.Blending, Target(PropertyRef.Effect(_music.Id, 0, "cutoff_hz"), 500f));
        Rebuild();
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.StartSnapshot("Muffle");
        _mixer.Update(0.016);

        List<EffectSetParameter> parameters = Emitted<EffectSetParameter>();
        Assert.That(parameters, Has.Count.EqualTo(1));
        Assert.That(parameters[0], Is.EqualTo(new EffectSetParameter("Music", 0, "cutoff_hz", 500f)));
        Assert.That(Emitted<BusSetVolume>(), Is.Empty, "no volume change");

        _commands.Clear();
        _mixer.Update(0.016);
        Assert.That(_commands.Count, Is.Zero);
    }

    [Test]
    public void MuteAndSoloOverrides_EmitOnChangeOnly()
    {
        _mixer.Update(0.016);
        _commands.Clear();

        _mixer.SetBusMute("SFX", true);
        _mixer.SetBusSolo("Music", true);
        _mixer.Update(0.016);

        Assert.That(_commands.Count, Is.EqualTo(2));
        Assert.That(Emitted<BusSetMute>()[0], Is.EqualTo(new BusSetMute("SFX", true)));
        Assert.That(Emitted<BusSetSolo>()[0], Is.EqualTo(new BusSetSolo("Music", true)));

        _commands.Clear();
        _mixer.SetBusMute("SFX", true);
        _mixer.Update(0.016);
        Assert.That(_commands.Count, Is.Zero);
    }

    [Test]
    public void Automation_OnGlobalParameter_AddsCurveValueToBusVolume()
    {
        var lane = new AutomationLane { Target = PropertyRef.BusVolume(_music.Id), Parameter = _intensityParam.Id };
        lane.Curve.Points.Add(new CurvePoint(0f, 0f));
        lane.Curve.Points.Add(new CurvePoint(1f, -10f));
        _mixer.AddAutomation(lane);
        _mixer.Update(0.016);
        _commands.Clear();

        Assert.That(_globals.TryGet("Intensity", out ParameterState state), Is.True);
        state.Set(0.5f, immediate: true);
        _mixer.Update(0.016);

        Assert.That(Volume("Music"), Is.EqualTo(-5f).Within(Tolerance));

        Assert.That(_mixer.RemoveAutomation(lane.Id), Is.True);
        _mixer.Update(0.016);
        Assert.That(Volume("Music"), Is.EqualTo(0f).Within(Tolerance));
    }

    [Test]
    public void Automation_UnknownTarget_Throws()
    {
        var lane = new AutomationLane { Target = PropertyRef.BusVolume(DirgeId.New()), Parameter = _intensityParam.Id };
        Assert.That((Action)(() => _mixer.AddAutomation(lane)), Throws.ArgumentException);
        var noParam = new AutomationLane { Target = PropertyRef.BusVolume(_music.Id), Parameter = DirgeId.New() };
        Assert.That((Action)(() => _mixer.AddAutomation(noParam)), Throws.ArgumentException);
    }

    [Test]
    public void BusPitch_EmitsOnPitchShiftEffectWhenPresent()
    {
        var shift = new BusEffect { TypeName = "AudioEffectPitchShift" };
        _sfx.Effects.Add(shift);
        AddSnapshot("Slow", 0, SnapshotMode.Blending, Target(PropertyRef.BusPitch(_sfx.Id), 0.5f), Target(PropertyRef.BusPitch(_music.Id), 0.5f));
        Rebuild();
        _mixer.Update(0.016);

        List<EffectSetParameter> initial = Emitted<EffectSetParameter>();
        Assert.That(initial, Does.Contain(new EffectSetParameter("SFX", 0, "pitch_scale", 1f)));
        _commands.Clear();

        _mixer.StartSnapshot("Slow");
        _mixer.Update(0.016);

        List<EffectSetParameter> parameters = Emitted<EffectSetParameter>();
        Assert.That(parameters, Has.Count.EqualTo(1), "Music has no pitch shift effect: its bus pitch is ignored");
        Assert.That(parameters[0], Is.EqualTo(new EffectSetParameter("SFX", 0, "pitch_scale", 0.5f)));
        Assert.That(_mixer.EffectiveBusPitch(_sfx.Id), Is.EqualTo(0.5f).Within(Tolerance));
    }

    [Test]
    public void BusPitch_MergesIntoAuthoredPitchScaleParameter()
    {
        var shift = new BusEffect { TypeName = "AudioEffectPitchShift" };
        shift.Parameters["pitch_scale"] = 2f;
        _sfx.Effects.Add(shift);
        AddSnapshot("Slow", 0, SnapshotMode.Blending, Target(PropertyRef.BusPitch(_sfx.Id), 0.5f));
        Rebuild();
        _mixer.Update(0.016);
        Assert.That(Emitted<EffectSetParameter>(), Has.Count.EqualTo(3), "two lowpass params + one pitch_scale, not duplicated");
        _commands.Clear();

        _mixer.StartSnapshot("Slow");
        _mixer.Update(0.016);

        List<EffectSetParameter> parameters = Emitted<EffectSetParameter>();
        Assert.That(parameters, Has.Count.EqualTo(1));
        Assert.That(parameters[0], Is.EqualTo(new EffectSetParameter("SFX", 0, "pitch_scale", 1f)));
    }

    [Test]
    public void Lookups_ByIdAndName()
    {
        Assert.That(_mixer.BusName(_sfx.Id), Is.EqualTo("SFX"));
        Assert.That(_mixer.TryGetBus("Music", out DirgeId id), Is.True);
        Assert.That(id, Is.EqualTo(_music.Id));
        Assert.That(_mixer.TryGetBus("Nope", out _), Is.False);
        Assert.That(_mixer.TryGetVca("Main", out DirgeId vcaId), Is.True);
        Assert.That(vcaId, Is.EqualTo(_vca.Id));
        Assert.That((Action)(() => _mixer.SetBusVolume("Nope", 0f)), Throws.TypeOf<KeyNotFoundException>());
        Assert.That((Action)(() => _mixer.StartSnapshot("Nope")), Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void SnapshotInstances_ArePooled()
    {
        AddSnapshot("Duck", 0, SnapshotMode.Blending, Target(PropertyRef.BusVolume(_music.Id), -12f));
        Rebuild();

        SnapshotInstance first = _mixer.StartSnapshot("Duck");
        _mixer.Update(0.016);
        first.Stop();
        _mixer.Update(0.016);
        Assert.That(first.IsActive, Is.False);

        SnapshotInstance second = _mixer.StartSnapshot("Duck");
        Assert.That(second, Is.SameAs(first), "released instance is recycled");
        Assert.That(second.Intensity, Is.EqualTo(1f));
        Assert.That(second.IsActive, Is.True);
    }
}
