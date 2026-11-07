using Dirge.Project;
using Dirge.Tool.Mixer;
using NUnit.Framework;

namespace Dirge.Model.Tests.Tool;

[TestFixture]
public sealed class MixerGraphOpsTests
{
    private AudioProject _project = null!;
    private Bus _master = null!, _music = null!, _sfx = null!, _ui = null!;
    private Snapshot _snapshot = null!;
    private AudioEvent _event = null!;

    [SetUp]
    public void SetUp()
    {
        _project = new AudioProject { Name = "Ops" };
        _master = new Bus { Name = "Master" };
        _music = new Bus { Name = "Music", Parent = _master.Id };
        _sfx = new Bus { Name = "SFX", Parent = _master.Id };
        _ui = new Bus { Name = "UI", Parent = _sfx.Id };
        _project.Mixer.Buses.Add(_master);
        _project.Mixer.Buses.Add(_music);
        _project.Mixer.Buses.Add(_sfx);
        _project.Mixer.Buses.Add(_ui);
        _music.Effects.Add(new BusEffect { TypeName = "AudioEffectLowPassFilter" });
        _music.Effects.Add(new BusEffect { TypeName = "AudioEffectReverb" });
        _music.Effects.Add(new BusEffect { TypeName = "AudioEffectCompressor" });
        _snapshot = new Snapshot { Name = "Snap" };
        _snapshot.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(_music.Id, 0, "cutoff_hz"), Value = 800f });
        _snapshot.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(_music.Id, 2, "threshold"), Value = -12f });
        _snapshot.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(_sfx.Id), Value = -20f });
        _project.Snapshots.Add(_snapshot);
        _event = new AudioEvent { Path = "sfx/ui", Bus = _sfx.Id };
        _project.Events.Add(_event);
    }

    [Test]
    public void RoutingTargets_ExcludeSelfAndDescendants()
    {
        var targets = new System.Collections.Generic.List<Bus>();
        MixerGraphOps.RoutingTargets(_project.Mixer, _sfx.Id, targets);
        Assert.That(targets, Is.EquivalentTo(new[] { _master, _music }));
        Assert.That(MixerGraphOps.IsSelfOrDescendant(_project.Mixer, _ui.Id, _sfx.Id), Is.True);
        Assert.That(MixerGraphOps.IsSelfOrDescendant(_project.Mixer, _music.Id, _sfx.Id), Is.False);
    }

    [Test]
    public void UniqueBusName_AppendsCounter()
    {
        Assert.That(MixerGraphOps.UniqueBusName(_project.Mixer, "Bus"), Is.EqualTo("Bus"));
        Assert.That(MixerGraphOps.UniqueBusName(_project.Mixer, "Music"), Is.EqualTo("Music 2"));
    }

    [Test]
    public void DeleteBus_ReparentsChildrenReroutesEventsDropsValues_AndRevertsExactly()
    {
        int sfxIndex = _project.Mixer.Buses.IndexOf(_sfx);
        MixerGraphOps.BusDeletion? plan = MixerGraphOps.PlanDeleteBus(_project, _sfx.Id);
        Assert.That(plan, Is.Not.Null);
        MixerGraphOps.ApplyDeleteBus(_project, plan!);

        Assert.That(_project.Mixer.Buses, Does.Not.Contain(_sfx));
        Assert.That(_ui.Parent, Is.EqualTo(_master.Id));
        Assert.That(_event.Bus, Is.EqualTo(_master.Id));
        Assert.That(_snapshot.Values, Has.Count.EqualTo(2));
        Assert.That(_snapshot.Values.Exists(v => v.Target.Owner == _sfx.Id), Is.False);

        MixerGraphOps.RevertDeleteBus(_project, plan!);
        Assert.That(_project.Mixer.Buses.IndexOf(_sfx), Is.EqualTo(sfxIndex));
        Assert.That(_ui.Parent, Is.EqualTo(_sfx.Id));
        Assert.That(_event.Bus, Is.EqualTo(_sfx.Id));
        Assert.That(_snapshot.Values, Has.Count.EqualTo(3));
        Assert.That(_snapshot.Values[2].Target, Is.EqualTo(PropertyRef.BusVolume(_sfx.Id)));
    }

    [Test]
    public void DeleteBus_RefusesMaster()
    {
        Assert.That(MixerGraphOps.PlanDeleteBus(_project, _master.Id), Is.Null);
    }

    [Test]
    public void MoveEffect_RemapsSnapshotIndices_AndIsInvertible()
    {
        MixerGraphOps.MoveEffect(_project, _music.Id, 0, 2);
        Assert.That(_music.Effects[2].TypeName, Is.EqualTo("AudioEffectLowPassFilter"));
        Assert.That(_music.Effects[0].TypeName, Is.EqualTo("AudioEffectReverb"));
        Assert.That(ValueFor("cutoff_hz").Target.EffectIndex, Is.EqualTo(2));
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(1));

        MixerGraphOps.MoveEffect(_project, _music.Id, 2, 0);
        Assert.That(_music.Effects[0].TypeName, Is.EqualTo("AudioEffectLowPassFilter"));
        Assert.That(ValueFor("cutoff_hz").Target.EffectIndex, Is.EqualTo(0));
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(2));
    }

    [Test]
    public void RemoveEffect_DropsItsValuesShiftsLaterOnes_AndReverts()
    {
        MixerGraphOps.EffectRemoval? plan = MixerGraphOps.PlanRemoveEffect(_project, _music.Id, 0);
        Assert.That(plan, Is.Not.Null);
        MixerGraphOps.ApplyRemoveEffect(_project, plan!);
        Assert.That(_music.Effects, Has.Count.EqualTo(2));
        Assert.That(_snapshot.Values.Exists(v => v.Target.Name == "cutoff_hz"), Is.False);
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(1));

        MixerGraphOps.RevertRemoveEffect(_project, plan!);
        Assert.That(_music.Effects, Has.Count.EqualTo(3));
        Assert.That(_music.Effects[0].TypeName, Is.EqualTo("AudioEffectLowPassFilter"));
        Assert.That(ValueFor("cutoff_hz").Target.EffectIndex, Is.EqualTo(0));
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(2));
    }

    [Test]
    public void InsertEffect_ShiftsValues_AndUninsertRestores()
    {
        var inserted = new BusEffect { TypeName = "AudioEffectDelay" };
        MixerGraphOps.InsertEffect(_project, _music.Id, inserted, 1);
        Assert.That(_music.Effects[1], Is.SameAs(inserted));
        Assert.That(ValueFor("cutoff_hz").Target.EffectIndex, Is.EqualTo(0));
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(3));

        MixerGraphOps.UninsertEffect(_project, _music.Id, inserted);
        Assert.That(_music.Effects, Has.Count.EqualTo(3));
        Assert.That(ValueFor("threshold").Target.EffectIndex, Is.EqualTo(2));
    }

    [Test]
    public void SnapshotValues_ScopeRestoreUnscope()
    {
        PropertyRef target = PropertyRef.BusVolume(_music.Id);
        Assert.That(MixerGraphOps.IsScoped(_snapshot, target), Is.False);
        float? previous = MixerGraphOps.SetValue(_snapshot, target, -6f);
        Assert.That(previous, Is.Null);
        Assert.That(MixerGraphOps.FindValue(_snapshot, target)!.Value, Is.EqualTo(-6f));

        previous = MixerGraphOps.SetValue(_snapshot, target, -9f);
        Assert.That(previous, Is.EqualTo(-6f));
        MixerGraphOps.RestoreValue(_snapshot, target, previous);
        Assert.That(MixerGraphOps.FindValue(_snapshot, target)!.Value, Is.EqualTo(-6f));

        MixerGraphOps.RestoreValue(_snapshot, target, null);
        Assert.That(MixerGraphOps.IsScoped(_snapshot, target), Is.False);
        Assert.That(MixerGraphOps.Unscope(_snapshot, target), Is.Null);
    }

    [Test]
    public void MoveBus_ReordersList()
    {
        MixerGraphOps.MoveBus(_project.Mixer, 3, 1);
        Assert.That(_project.Mixer.Buses[1], Is.SameAs(_ui));
        Assert.That(_project.Mixer.Buses[2], Is.SameAs(_music));
        MixerGraphOps.MoveBus(_project.Mixer, 1, 3);
        Assert.That(_project.Mixer.Buses[3], Is.SameAs(_ui));
    }

    [Test]
    public void RemoveEffect_ShiftsFaderBehindIt_AndRevertRestores()
    {
        _music.FaderIndex = 2; // [LowPass, Reverb, FADER, Compressor]
        MixerGraphOps.EffectRemoval plan = MixerGraphOps.PlanRemoveEffect(_project, _music.Id, 0)!;
        MixerGraphOps.ApplyRemoveEffect(_project, plan);
        Assert.That(_music.FaderIndex, Is.EqualTo(1), "fader stays between Reverb and Compressor");
        MixerGraphOps.RevertRemoveEffect(_project, plan);
        Assert.That(_music.FaderIndex, Is.EqualTo(2));

        plan = MixerGraphOps.PlanRemoveEffect(_project, _music.Id, 2)!;
        MixerGraphOps.ApplyRemoveEffect(_project, plan);
        Assert.That(_music.FaderIndex, Is.EqualTo(2), "removing a post-fader effect leaves the fader");
        MixerGraphOps.RevertRemoveEffect(_project, plan);
        Assert.That(_music.FaderIndex, Is.EqualTo(2));
    }

    [Test]
    public void InsertEffect_ShiftsFaderAtOrAfterSlot_DefaultStaysAtEnd()
    {
        var added = new BusEffect { TypeName = "AudioEffectDelay" };
        MixerGraphOps.InsertEffect(_project, _music.Id, added, 3);
        Assert.That(_music.FaderIndex, Is.EqualTo(int.MaxValue), "default fader keeps 'after everything'");
        MixerGraphOps.UninsertEffect(_project, _music.Id, added);

        _music.FaderIndex = 1; // [LowPass, FADER, Reverb, Compressor]
        MixerGraphOps.InsertEffect(_project, _music.Id, added, 1);
        Assert.That(_music.FaderIndex, Is.EqualTo(2), "new effect lands pre-fader");
        Assert.That(_music.Effects[1], Is.SameAs(added));
        MixerGraphOps.UninsertEffect(_project, _music.Id, added);
        Assert.That(_music.FaderIndex, Is.EqualTo(1));

        MixerGraphOps.InsertEffect(_project, _music.Id, added, 2);
        Assert.That(_music.FaderIndex, Is.EqualTo(1), "insert after the fader leaves it");
        MixerGraphOps.UninsertEffect(_project, _music.Id, added);
        Assert.That(_music.FaderIndex, Is.EqualTo(1));
    }

    private SnapshotValue ValueFor(string parameter) => _snapshot.Values.Find(v => v.Target.Name == parameter)!;
}
