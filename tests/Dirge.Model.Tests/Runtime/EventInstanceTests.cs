using System.Collections.Generic;
using System.Numerics;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Runtime;

/// <summary>Drives a <see cref="DirgeSystem"/> like a driver would and keeps every emitted command.</summary>
internal sealed class Harness
{
    public readonly DirgeSystem System;
    public readonly List<Command> Log = new();
    public readonly List<CallbackEvent> Callbacks = new();

    public Harness(AudioProject project, int seed = 1)
    {
        System = new DirgeSystem(project, seed);
    }

    public void Step(double dt, int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            System.Update(dt);
            Log.AddRange(System.Commands.Commands);
            System.Commands.Clear();
        }
    }

    public EventInstance Create(string path)
    {
        EventInstance instance = System.GetEvent(path).CreateInstance();
        instance.Callback += (_, e) => Callbacks.Add(e);
        return instance;
    }

    public int Count<T>() where T : Command
    {
        int n = 0;
        foreach (Command c in Log) if (c is T) n++;
        return n;
    }

    public List<T> All<T>() where T : Command
    {
        var list = new List<T>();
        foreach (Command c in Log) if (c is T t) list.Add(t);
        return list;
    }

    public int CallbackCount<T>() where T : CallbackEvent
    {
        int n = 0;
        foreach (CallbackEvent e in Callbacks) if (e is T) n++;
        return n;
    }
}

[TestFixture]
public sealed class EventInstanceTests
{
    private const double Tolerance = 1e-6;

    [Test]
    public void TimelineInstrument_TriggersOnce_WithSummedGainChain()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Gain");
        ev.Macros.VolumeDb = -3f;
        Track track = b.AddTrack(ev, volumeDb: -2f);
        ClipInstrument clip = b.AddClip(track, "res://a.wav");
        clip.VolumeDb = -1f;
        b.Project.AssetLengths["res://a.wav"] = 5.0;

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Gain");
        instance.SetVolume(-4f);
        instance.Start();
        h.Step(0.1, 10);

        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(1));
        Assert.That(plays[0].VolumeDb, Is.EqualTo(-10f).Within(1e-4f));
        Assert.That(plays[0].Asset.Path, Is.EqualTo("res://a.wav"));
        Assert.That(plays[0].StartSeconds, Is.EqualTo(0).Within(Tolerance));
        Assert.That(plays[0].Bus, Is.EqualTo("Master"));
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.CallbackCount<StartedCallback>(), Is.EqualTo(1));
        Assert.That(h.CallbackCount<SoundPlayedCallback>(), Is.EqualTo(1));
    }

    [Test]
    public void SheetInstrument_TriggersWhenSmoothedValueEntersRange_AndCutsOnExit()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Sheet");
        ev.Macros.Persistent = true;
        ParameterDefinition p = b.AddLocalParameter(ev, "Intensity");
        p.SeekSpeedUp = 1f;
        Track track = b.AddTrack(ev);
        b.AddSheetInstrument(track, new ClipInstrument { Asset = new AssetRef("res://loop.wav"), Loop = true }, p, 0.5f, 0.5f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Sheet");
        instance.Start();
        instance.SetParameter("Intensity", 1f);
        h.Step(0.1, 3); // value ≈ 0.3
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(0));
        h.Step(0.1, 3); // value ≈ 0.6
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(0));

        instance.SetParameter("Intensity", 0f, immediate: true);
        h.Step(0.1);
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(1));
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing), "persistent events outlive their voices");
    }

    [Test]
    public void AsyncSheetInstrument_KeepsPlayingAfterExit()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Async");
        ev.Macros.Persistent = true;
        ParameterDefinition p = b.AddLocalParameter(ev, "Hit");
        Track track = b.AddTrack(ev);
        b.AddSheetInstrument(track, new ClipInstrument { Asset = new AssetRef("res://hit.wav"), Async = true }, p, 0.5f, 0.5f);
        b.Project.AssetLengths["res://hit.wav"] = 2.0;

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Async");
        instance.Start();
        instance.SetParameter("Hit", 1f, immediate: true);
        h.Step(0.1);
        instance.SetParameter("Hit", 0f, immediate: true);
        h.Step(0.1, 5);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(0));
    }

    [Test]
    public void LoopRegion_WrapsAndRetriggersInstrument()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Loop");
        b.AddClip(b.AddTrack(ev), "res://short.wav");
        b.Project.AssetLengths["res://short.wav"] = 0.5;
        b.AddMarker(ev, new LoopRegion { Length = 1f }, 0f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Loop");
        instance.Start();
        h.Step(0.25, 4); // t = 1.0 → wrap
        Assert.That(instance.TimelinePosition, Is.EqualTo(0).Within(Tolerance));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(2));
        h.Step(0.25, 4);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(3));
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.System.GetEvent("Test/Loop").IsOneshot, Is.False);
    }

    [Test]
    public void TransitionMarker_BeatQuantized_JumpsOnNextBeat()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Music");
        b.AddTempo(ev, bpm: 120f);
        b.AddClip(b.AddTrack(ev), "res://music.wav");
        b.Project.AssetLengths["res://music.wav"] = 4.0;
        DestinationMarker dest = b.AddMarker(ev, new DestinationMarker(), 2f, "Chorus");
        b.AddMarker(ev, new TransitionMarker { Destination = dest.Id, Quantization = Quantization.Beat }, 0.3f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Music");
        instance.Start();
        h.Step(0.1, 4); // t = 0.4: marker reached at 0.3, next beat is 0.5
        Assert.That(instance.TimelinePosition, Is.EqualTo(0.4).Within(1e-9));
        h.Step(0.1);    // t = 0.5 → jump to 2.0
        Assert.That(instance.TimelinePosition, Is.EqualTo(2.0).Within(1e-9));
        h.Step(0.1);
        Assert.That(instance.TimelinePosition, Is.EqualTo(2.1).Within(1e-9));

        List<VoiceSeek> seeks = h.All<VoiceSeek>();
        Assert.That(seeks, Has.Count.EqualTo(1));
        Assert.That(seeks[0].Seconds, Is.EqualTo(2.0).Within(1e-9));
        Assert.That(h.CallbackCount<MarkerCallback>(), Is.EqualTo(1), "destination marker fires on arrival");

        int beats = 0;
        foreach (CallbackEvent e in h.Callbacks) if (e is BeatCallback) beats++;
        Assert.That(beats, Is.EqualTo(3), "beats at 0.0, 0.5 (pre-jump) and 2.0 (destination)");
    }

    [Test]
    public void SustainPoint_HoldsUntilKeyOff()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Sustain");
        b.AddClip(b.AddTrack(ev), "res://pad.wav");
        b.Project.AssetLengths["res://pad.wav"] = 3.0;
        b.AddMarker(ev, new SustainPoint(), 0.5f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Sustain");
        instance.Start();
        h.Step(0.25, 2);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Sustaining));
        Assert.That(instance.TimelinePosition, Is.EqualTo(0.5).Within(Tolerance));
        h.Step(0.25, 4);
        Assert.That(instance.TimelinePosition, Is.EqualTo(0.5).Within(Tolerance));
        instance.KeyOff();
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        h.Step(0.25);
        Assert.That(instance.TimelinePosition, Is.EqualTo(0.75).Within(Tolerance));
    }

    [Test]
    public void MaxInstancesOne_Oldest_StealsWithCallbackAndStop()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Steal");
        ev.Macros.MaxInstances = 1;
        ev.Macros.Stealing = StealingMode.Oldest;
        b.AddClip(b.AddTrack(ev), "res://long.wav");
        b.Project.AssetLengths["res://long.wav"] = 10.0;

        var h = new Harness(b.Build());
        EventInstance first = h.Create("Test/Steal");
        first.Start();
        h.Step(0.1);
        VoiceId firstVoice = h.All<VoicePlay>()[0].Voice;

        EventInstance second = h.Create("Test/Steal");
        second.Start();
        Assert.That(first.State, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(h.CallbackCount<StolenCallback>(), Is.EqualTo(1));
        Assert.That(h.CallbackCount<StoppedCallback>(), Is.EqualTo(1));
        Assert.That(h.System.Commands.Commands, Has.Exactly(1).InstanceOf<VoiceStop>());
        Assert.That(((VoiceStop)h.System.Commands.Commands[0]).Voice, Is.EqualTo(firstVoice));

        h.Step(0.1);
        Assert.That(second.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(2));
        Assert.That(h.System.GetEvent("Test/Steal").InstanceCount, Is.EqualTo(2));
    }

    [Test]
    public void StealingNone_StartIsNoOp()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/None");
        ev.Macros.MaxInstances = 1;
        ev.Macros.Stealing = StealingMode.None;
        b.AddClip(b.AddTrack(ev), "res://long.wav");
        b.Project.AssetLengths["res://long.wav"] = 10.0;

        var h = new Harness(b.Build());
        h.Create("Test/None").Start();
        EventInstance second = h.Create("Test/None");
        second.Start();
        Assert.That(second.State, Is.EqualTo(PlaybackState.Stopped));
        h.Step(0.1);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));
    }

    [Test]
    public void MaxRealVoices_VirtualizesByPriority()
    {
        var b = new ProjectBuilder();
        AudioEvent important = b.AddEvent("Test/Important");
        important.Macros.Priority = 10;
        b.AddClip(b.AddTrack(important), "res://imp.wav");
        AudioEvent minor = b.AddEvent("Test/Minor");
        minor.Macros.Priority = 200;
        b.AddClip(b.AddTrack(minor), "res://min.wav");
        b.Project.AssetLengths["res://imp.wav"] = 10.0;
        b.Project.AssetLengths["res://min.wav"] = 10.0;

        var h = new Harness(b.Build());
        h.System.MaxRealVoices = 1;
        EventInstance minorInstance = h.Create("Test/Minor");
        minorInstance.Start();
        EventInstance importantInstance = h.Create("Test/Important");
        importantInstance.Start();
        h.Step(0.1);

        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(1));
        Assert.That(plays[0].Asset.Path, Is.EqualTo("res://imp.wav"));
        Assert.That(minorInstance.IsVirtual, Is.True);
        Assert.That(importantInstance.IsVirtual, Is.False);
        Assert.That(h.System.RealVoiceCount, Is.EqualTo(1));
        Assert.That(h.CallbackCount<VirtualizedCallback>(), Is.EqualTo(1));

        importantInstance.Stop(StopMode.Immediate);
        h.Step(0.1);
        plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(2));
        Assert.That(plays[1].Asset.Path, Is.EqualTo("res://min.wav"));
        Assert.That(plays[1].StartSeconds, Is.EqualTo(0.1).Within(1e-9), "virtual voice kept its position");
        Assert.That(minorInstance.IsVirtual, Is.False);
        Assert.That(h.CallbackCount<UnvirtualizedCallback>(), Is.EqualTo(1));
    }

    [Test]
    public void MultiRandom_AvoidRepeat_NeverRepeatsWithinWindow()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Multi");
        var multi = new MultiInstrument { Mode = PlaylistMode.Random, AvoidRepeat = 2 };
        for (int i = 0; i < 4; i++)
        {
            string asset = $"res://m{i}.wav";
            multi.Entries.Add(new MultiEntry { Asset = new AssetRef(asset) });
            b.Project.AssetLengths[asset] = 0.1;
        }
        b.AddInstrument(b.AddTrack(ev), multi);
        b.AddMarker(ev, new LoopRegion { Length = 0.2f }, 0f);

        var h = new Harness(b.Build(), seed: 42);
        h.Create("Test/Multi").Start();
        h.Step(0.05, 260); // one retrigger per 0.2 s loop

        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays.Count, Is.GreaterThanOrEqualTo(50));
        for (int i = 1; i < plays.Count; i++)
        {
            Assert.That(plays[i].Asset, Is.Not.EqualTo(plays[i - 1].Asset), $"repeat at {i}");
            if (i >= 2) Assert.That(plays[i].Asset, Is.Not.EqualTo(plays[i - 2].Asset), $"repeat within window at {i}");
        }
    }

    [Test]
    public void MultiSequential_CyclesEntriesInOrder()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Seq");
        var multi = new MultiInstrument { Mode = PlaylistMode.Sequential };
        for (int i = 0; i < 3; i++)
        {
            multi.Entries.Add(new MultiEntry { Asset = new AssetRef($"res://s{i}.wav") });
            b.Project.AssetLengths[$"res://s{i}.wav"] = 0.1;
        }
        b.AddInstrument(b.AddTrack(ev), multi);
        b.AddMarker(ev, new LoopRegion { Length = 0.2f }, 0f);

        var h = new Harness(b.Build());
        h.Create("Test/Seq").Start();
        h.Step(0.05, 40);
        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays.Count, Is.GreaterThanOrEqualTo(7));
        for (int i = 0; i < plays.Count; i++) Assert.That(plays[i].Asset.Path, Is.EqualTo($"res://s{i % 3}.wav"));
    }

    [Test]
    public void OneShot_EndsFromCatalogLength_WithStoppedCallback()
    {
        var b = ProjectBuilder.Minimal("Test/Beep", "res://beep.wav", 1.0);
        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Beep");
        instance.Start();
        h.Step(0.25, 3);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        h.Step(0.25);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(1));
        Assert.That(h.CallbackCount<StoppedCallback>(), Is.EqualTo(1));
        Assert.That(h.CallbackCount<SoundStoppedCallback>(), Is.EqualTo(1));
        Assert.That(h.System.GetEvent("Test/Beep").IsOneshot, Is.True);
        Assert.That(h.System.GetEvent("Test/Beep").Length, Is.EqualTo(1.0).Within(Tolerance));

        // A late VoiceFinished for a model-scheduled end is ignored and must not affect a new voice.
        h.System.Feedback(new VoiceFinished(new VoiceId(0)));
        h.Step(0.1);
        Assert.That(h.System.RealVoiceCount, Is.EqualTo(0));
    }

    [Test]
    public void OneShot_UnknownLength_WaitsForVoiceFinished()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Unknown");
        b.AddClip(b.AddTrack(ev), "res://mystery.wav");

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Unknown");
        instance.Start();
        h.Step(0.1, 50);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        VoiceId voice = h.All<VoicePlay>()[0].Voice;
        h.System.Feedback(new VoiceFinished(voice));
        h.Step(0.1);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(0), "engine already finished it");
    }

    [Test]
    public void ReleasedInstance_IsPooledAfterStop()
    {
        var b = ProjectBuilder.Minimal("Test/Beep", "res://beep.wav", 0.5);
        var h = new Harness(b.Build());
        EventDescription description = h.System.GetEvent("Test/Beep");
        EventInstance instance = h.Create("Test/Beep");
        instance.Start();
        instance.Release();
        Assert.That(h.System.InstanceCount, Is.EqualTo(1));
        h.Step(0.25, 3);
        Assert.That(h.System.InstanceCount, Is.EqualTo(0));
        Assert.That(description.InstanceCount, Is.EqualTo(0));
        EventInstance again = description.CreateInstance();
        Assert.That(again, Is.SameAs(instance), "pooled instance is reused");
        Assert.That(again.State, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void StopAllowFadeout_FadesThenStops()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Fade");
        ClipInstrument clip = b.AddClip(b.AddTrack(ev), "res://pad.wav");
        clip.FadeOut = 0.5f;
        b.Project.AssetLengths["res://pad.wav"] = 10.0;

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Fade");
        instance.Start();
        h.Step(0.1);
        instance.Stop(StopMode.AllowFadeout);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Stopping));
        h.Step(0.25);
        List<VoiceSetVolume> volumes = h.All<VoiceSetVolume>();
        Assert.That(volumes, Has.Count.EqualTo(1));
        Assert.That(volumes[0].VolumeDb, Is.EqualTo(Dirge.Evaluation.CurveMath.LinearToDb(0.5f)).Within(1e-3f));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(0));
        h.Step(0.25);
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(1));
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Stopped));
    }

    [Test]
    public void NestedEventInstrument_PlaysChildWithInheritedGain()
    {
        var b = new ProjectBuilder();
        AudioEvent child = b.AddEvent("Test/Child");
        child.Macros.VolumeDb = -1f;
        b.AddClip(b.AddTrack(child), "res://child.wav");
        b.Project.AssetLengths["res://child.wav"] = 1.0;
        AudioEvent parent = b.AddEvent("Test/Parent");
        parent.Macros.VolumeDb = -2f;
        EventInstrument nested = b.AddInstrument(b.AddTrack(parent), new EventInstrument { Event = child.Id }, 0f, 1f);
        nested.VolumeDb = -3f;

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Parent");
        instance.Start();
        h.Step(0.25);
        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(1));
        Assert.That(plays[0].Asset.Path, Is.EqualTo("res://child.wav"));
        Assert.That(plays[0].VolumeDb, Is.EqualTo(-6f).Within(1e-4f));
        Assert.That(h.System.InstanceCount, Is.EqualTo(2));
        h.Step(0.25, 2); // t = 0.75: the 1 s child clip is still playing (a double-updated child would have ended)
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(0));
        h.Step(0.25, 2);
        Assert.That(instance.State, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(h.System.InstanceCount, Is.EqualTo(1), "child recycled when it finished");
    }

    [Test]
    public void SpatialBuiltIn_DistanceDrivesSheetInstrument_AndEmitsSet3D()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Spatial");
        ev.Macros.Is3D = true;
        ev.Macros.Persistent = true;
        ParameterDefinition distance = b.AddLocalParameter(ev, "Distance", 0f, 100f);
        distance.BuiltIn = BuiltInParameter.Distance;
        b.AddSheetInstrument(b.AddTrack(ev), new ClipInstrument { Asset = new AssetRef("res://near.wav"), Loop = true }, distance, 0f, 5f);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Spatial");
        instance.Set3DAttributes(new Attributes3D(new Vector3(10f, 0f, 0f), Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
        instance.Start();
        h.Step(0.1);
        Assert.That(instance.GetParameter("Distance"), Is.EqualTo(10f).Within(1e-4f));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(0));

        instance.Set3DAttributes(new Attributes3D(new Vector3(2f, 0f, 0f), Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
        h.Step(0.1);
        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(1));
        Assert.That(plays[0].Spatial, Is.True);
        Assert.That(plays[0].Attributes.Position.X, Is.EqualTo(2f));

        instance.Set3DAttributes(new Attributes3D(new Vector3(3f, 0f, 0f), Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
        h.Step(0.1);
        List<VoiceSet3D> moves = h.All<VoiceSet3D>();
        Assert.That(moves, Has.Count.EqualTo(1));
        Assert.That(moves[0].Attributes.Position.X, Is.EqualTo(3f));
    }

    [Test]
    public void SameSeed_ProducesIdenticalCommandSequence()
    {
        static AudioProject Build()
        {
            var b = new ProjectBuilder();
            AudioEvent ev = b.AddEvent("Test/Rand");
            var multi = new MultiInstrument { Mode = PlaylistMode.Random, AvoidRepeat = 1, VolumeRandomDb = 6f, PitchRandom = 0.2f };
            for (int i = 0; i < 5; i++)
            {
                multi.Entries.Add(new MultiEntry { Asset = new AssetRef($"res://r{i}.wav"), Weight = 1f + i });
                b.Project.AssetLengths[$"res://r{i}.wav"] = 0.1;
            }
            b.AddInstrument(b.AddTrack(ev), multi);
            b.AddMarker(ev, new LoopRegion { Length = 0.2f }, 0f);
            b.AddModulator(ev, new LfoModulator { Shape = LfoShape.Noise, RateHz = 7f }, PropertyRef.EventVolume(ev.Id), 3f);
            return b.Build();
        }

        var a = new Harness(Build(), seed: 7);
        var c = new Harness(Build(), seed: 7);
        a.Create("Test/Rand").Start();
        c.Create("Test/Rand").Start();
        a.Step(0.05, 80);
        c.Step(0.05, 80);
        Assert.That(a.Log.Count, Is.GreaterThan(40));
        Assert.That(c.Log, Is.EqualTo(a.Log));

        var d = new Harness(Build(), seed: 8);
        d.Create("Test/Rand").Start();
        d.Step(0.05, 80);
        Assert.That(d.Log, Is.Not.EqualTo(a.Log));
    }

    [Test]
    public void StealingVirtualize_NewInstanceIsVirtualUntilSlotFrees()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Virt");
        ev.Macros.MaxInstances = 1;
        ev.Macros.Stealing = StealingMode.Virtualize;
        b.AddClip(b.AddTrack(ev), "res://long.wav");
        b.Project.AssetLengths["res://long.wav"] = 10.0;

        var h = new Harness(b.Build());
        EventInstance first = h.Create("Test/Virt");
        first.Start();
        EventInstance second = h.Create("Test/Virt");
        second.Start();
        h.Step(0.1);
        Assert.That(first.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(second.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(second.IsVirtual, Is.True);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));

        first.Stop(StopMode.Immediate);
        h.Step(0.1);
        Assert.That(second.IsVirtual, Is.False);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(2));
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(1));
    }

    [Test]
    public void Scatterer_SpawnsVoicesAroundInstanceWithinDistance()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Scatter");
        ev.Macros.Is3D = true;
        ev.Macros.Persistent = true;
        var scatterer = new ScattererInstrument { MinInterval = 0.2f, MaxInterval = 0.2f, MinScatterDistance = 2f, MaxScatterDistance = 4f, Polyphony = 3 };
        scatterer.Entries.Add(new MultiEntry { Asset = new AssetRef("res://bird.wav") });
        b.Project.AssetLengths["res://bird.wav"] = 5.0;
        b.AddInstrument(b.AddTrack(ev), scatterer);

        var h = new Harness(b.Build(), seed: 3);
        EventInstance instance = h.Create("Test/Scatter");
        var center = new Vector3(10f, 1f, -5f);
        instance.Set3DAttributes(new Attributes3D(center, Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
        instance.Start();
        h.Step(0.1, 10); // t = 1.0 → spawns at 0, 0.2, 0.4, 0.6, 0.8

        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(5));
        foreach (VoicePlay play in plays)
        {
            Assert.That(play.Spatial, Is.True);
            float d = Vector3.Distance(play.Attributes.Position, center);
            Assert.That(d, Is.InRange(2f - 1e-3f, 4f + 1e-3f));
            Assert.That(play.Attributes.Position.Y, Is.EqualTo(center.Y));
        }
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(2), "polyphony 3 steals the oldest");
        Assert.That(h.System.RealVoiceCount, Is.EqualTo(3));
    }

    [Test]
    public void SteadyState_UpdateDoesNotAllocate()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Steady");
        ev.Macros.Persistent = true;
        ParameterDefinition p = b.AddLocalParameter(ev, "P");
        Track track = b.AddTrack(ev);
        b.AddClip(track, "res://loop.wav").Loop = true;
        b.Project.AssetLengths["res://loop.wav"] = 2.0;
        b.AddSheetInstrument(track, new ClipInstrument { Asset = new AssetRef("res://x.wav") }, p, 0.5f, 0.5f);
        b.AddMarker(ev, new LoopRegion { Length = 100f }, 0f);
        b.AddTempo(ev);

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Steady");
        instance.CallbackMask = CallbackMask.None;
        instance.Start();
        h.Step(0.016, 50);
        h.System.Commands.Clear();

        long before = System.GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) h.System.Update(0.016);
        long after = System.GC.GetAllocatedBytesForCurrentThread();
        Assert.That(h.System.Commands.Count, Is.EqualTo(0), "stable state emits nothing");
        Assert.That(after - before, Is.EqualTo(0));
    }
}
