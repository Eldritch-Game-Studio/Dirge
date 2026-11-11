using System.Linq;
using System.Numerics;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Runtime;

[TestFixture]
public sealed class SpatialRegionTests
{
    private static Attributes3D At(float x, int region = 0) =>
        Attributes3D.Origin with { Position = new Vector3(x, 0, 0), Region = new SpatialRegionId(region) };

    private static ProjectBuilder Build(bool scatter = false)
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Spatial");
        ev.Macros.Is3D = true;
        ev.Macros.Persistent = true;
        b.AddLocalParameter(ev, "Distance", 0, 1000).BuiltIn = BuiltInParameter.Distance;
        b.AddLocalParameter(ev, "Normalized").BuiltIn = BuiltInParameter.DistanceNormalized;
        if (scatter)
        {
            var instrument = new ScattererInstrument { MinInterval = 0.1f, MaxInterval = 0.1f, MinScatterDistance = 2, MaxScatterDistance = 2 };
            instrument.Entries.Add(new MultiEntry { Asset = new AssetRef("res://tone.wav") });
            b.AddInstrument(b.AddTrack(ev), instrument);
        }
        else b.AddInstrument(b.AddTrack(ev), new ClipInstrument { Asset = new AssetRef("res://tone.wav"), Loop = true });
        b.Project.AssetLengths["res://tone.wav"] = 10;
        return b;
    }

    private static EventInstance Start(DirgeSystem system, Attributes3D attributes)
    {
        EventInstance instance = system.GetEvent("Spatial").CreateInstance();
        instance.Set3DAttributes(attributes);
        instance.Start();
        return instance;
    }

    [Test]
    public void SameCoordinates_DoNotBorrowAnotherRegionsNearestListener()
    {
        var system = new DirgeSystem(Build().Build());
        system.SetListener(0, At(0, 1));
        system.SetListener(1, At(0, 2));
        EventInstance a = Start(system, At(0, 1));
        EventInstance b = Start(system, At(0, 2));
        system.Update(0.01);
        Assert.That(a.GetParameter("Distance"), Is.Zero);
        Assert.That(b.GetParameter("Distance"), Is.Zero);
        system.SetListener(0, At(25, 1));
        system.Update(0.01);
        Assert.That(a.GetParameter("Distance"), Is.EqualTo(25));
        Assert.That(b.GetParameter("Distance"), Is.Zero);
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Select(p => p.Attributes.Region.Value), Is.EquivalentTo(new[] { 1, 2 }));
    }

    [Test]
    public void MissingOrDisabledRegionListener_IsMaximallyDistant_NotDefaultOrigin()
    {
        var system = new DirgeSystem(Build().Build());
        EventInstance instance = Start(system, At(0, 7));
        system.Update(0.01);
        Assert.That(instance.GetParameter("Distance"), Is.EqualTo(1000));
        Assert.That(instance.GetParameter("Normalized"), Is.EqualTo(1));
        system.SetListener(1, At(10, 7));
        system.Update(0.01);
        Assert.That(instance.GetParameter("Distance"), Is.EqualTo(10));
        system.SetListener(1, At(10, 7), 0);
        system.Update(0.01);
        Assert.That(instance.GetParameter("Distance"), Is.EqualTo(1000));
    }

    [Test]
    public void FurthestStealing_UsesEachCandidatesOwnRegion()
    {
        ProjectBuilder builder = Build();
        AudioEvent ev = builder.Project.Events[0];
        ev.Macros.MaxInstances = 2;
        ev.Macros.Stealing = StealingMode.Furthest;
        var system = new DirgeSystem(builder.Build());
        system.SetListener(0, At(0, 1));
        system.SetListener(1, At(50, 2));
        EventInstance near = Start(system, At(1, 1));
        EventInstance far = Start(system, At(0, 2));
        system.Update(0.01);
        Start(system, At(0, 1));
        Assert.That(near.State, Is.Not.EqualTo(PlaybackState.Stopped));
        Assert.That(far.State, Is.EqualTo(PlaybackState.Stopped));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RegionMutation_ReachesLiveOrdinaryAndScatterVoices(bool scatter)
    {
        var system = new DirgeSystem(Build(scatter).Build());
        EventInstance instance = Start(system, At(0, 1));
        system.Update(0.2);
        VoicePlay[] initial = system.Commands.Commands.OfType<VoicePlay>().ToArray();
        Assert.That(initial, Is.Not.Empty);
        Assert.That(initial.All(p => p.Attributes.Region.Value == 1), Is.True);
        system.Commands.Clear();
        instance.Set3DAttributes(At(0, 2));
        system.Update(0.01);
        VoiceSet3D[] changes = system.Commands.Commands.OfType<VoiceSet3D>().ToArray();
        Assert.That(changes, Has.Length.EqualTo(initial.Length));
        Assert.That(changes.All(c => c.Attributes.Region.Value == 2), Is.True);
        foreach (VoiceSet3D change in changes)
            Assert.That(change.Attributes.Position, Is.EqualTo(initial.Single(p => p.Voice == change.Voice).Attributes.Position));
    }

    [Test]
    public void LegacyConstructorAndDeconstruct_KeepExactClrArities()
    {
        var vector = typeof(Vector3);
        var output = vector.MakeByRefType();
        Assert.That(typeof(Attributes3D).GetConstructor(new[] { vector, vector, vector, vector }), Is.Not.Null);
        Assert.That(typeof(Attributes3D).GetMethod("Deconstruct", new[] { output, output, output, output }), Is.Not.Null);
        Assert.That(typeof(VoicePlay).GetConstructors().Single().GetParameters(), Has.Length.EqualTo(15));
    }

    [Test]
    public void LegacyConstructorListenerFallbackAndInstanceReuse_RemainDefaultRegion()
    {
        var system = new DirgeSystem(Build().Build());
        var attributes = new Attributes3D(new Vector3(3, 0, 0), Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var (position, velocity, forward, up) = attributes;
        Assert.That(position.X, Is.EqualTo(3));
        Assert.That(attributes.Region, Is.EqualTo(default(SpatialRegionId)));
        system.SetListener(0, At(10), 0);
        EventInstance instance = Start(system, attributes);
        system.Update(0.01);
        Assert.That(instance.GetParameter("Distance"), Is.EqualTo(7));
        instance.Set3DAttributes(At(0, 77));
        instance.Stop(StopMode.Immediate);
        instance.Release();
        system.Update(0.01);
        system.Commands.Clear();
        EventInstance reused = system.GetEvent("Spatial").CreateInstance();
        reused.Start();
        system.Update(0.01);
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Single().Attributes.Region, Is.EqualTo(default(SpatialRegionId)));
    }
}
