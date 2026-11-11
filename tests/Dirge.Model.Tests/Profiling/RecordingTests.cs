using System.Collections.Generic;
using System.Numerics;
using Dirge.Profiling;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Profiling;

[TestFixture]
public sealed class RecordingTests
{
    /// <summary>Randomness (multi playlist + random volume), 3D, snapshots, programmer sounds, unknown-length voices.</summary>
    private static AudioProject SessionProject()
    {
        var b = new ProjectBuilder("Session");
        Bus music = b.AddBus("Music");
        Bus sfx = b.AddBus("SFX");
        b.AddEffect(music, "AudioEffectLowPassFilter", ("cutoff_hz", 20000f));
        ParameterDefinition intensity = b.AddGlobalParameter("Intensity");
        Snapshot duck = b.AddSnapshot("Duck", 1);
        duck.Intensity = new Ahdsr { Attack = 0.2f, Release = 0.3f };
        duck.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(music.Id), Value = -12f });
        duck.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(music.Id, 0, "cutoff_hz"), Value = 800f });

        AudioEvent loop = b.AddEvent("Music/Loop", music);
        loop.Macros.Persistent = true;
        b.AddInstrument(b.AddTrack(loop), new ClipInstrument { Asset = new AssetRef("res://loop.ogg"), Loop = true });
        b.AddAutomation(loop, PropertyRef.EventVolume(loop.Id), intensity, new CurvePoint(0f, -6f), new CurvePoint(1f, 0f));

        AudioEvent shot = b.AddEvent("SFX/Shot", sfx);
        shot.Macros.Is3D = true;
        var multi = b.AddInstrument(b.AddTrack(shot), new MultiInstrument { VolumeRandomDb = 3f, PitchRandom = 0.1f });
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://s1.ogg") });
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://s2.ogg") });
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://s3.ogg") });

        AudioEvent line = b.AddEvent("VO/Line", sfx);
        b.AddInstrument(b.AddTrack(line), new ProgrammerInstrument { Key = "line" });

        AudioEvent unknown = b.AddEvent("SFX/Unknown", sfx);
        b.AddClip(b.AddTrack(unknown), "res://unknown-length.ogg");

        AudioProject project = b.Build();
        project.AssetLengths["res://loop.ogg"] = 2.0;
        project.AssetLengths["res://s1.ogg"] = 0.4;
        project.AssetLengths["res://s2.ogg"] = 0.5;
        project.AssetLengths["res://s3.ogg"] = 0.6;
        project.AssetLengths["res://vo.ogg"] = 1.0;
        return project;
    }

    /// <summary>200 ticks exercising every recorded API surface; returns the recording.</summary>
    private static Recording RunSession(AudioProject project, int seed)
    {
        var system = new DirgeSystem(project, seed) { Locale = "en", MaxRealVoices = 8 };
        var recorder = new Recorder(system);
        EventInstance loop = system.GetEvent("Music/Loop").CreateInstance();
        loop.Start();
        SnapshotInstance? duck = null;
        EventInstance? vo = null;
        VoiceId? unknownVoice = null;
        var shots = new List<EventInstance>();
        for (int tick = 0; tick < 200; tick++)
        {
            double t = tick * 0.05;
            system.SetListener(0, new Attributes3D(new Vector3((float)t, 0f, 0f), Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY));
            if (tick % 7 == 0)
            {
                EventInstance shot = system.GetEvent("SFX/Shot").CreateInstance();
                shot.Set3DAttributes(new Attributes3D(new Vector3(0f, 0f, (float)(tick % 20)), Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
                shot.SetPitch(1f + tick % 3 * 0.1f);
                shot.Start();
                shot.Release();
                shots.Add(shot);
            }
            if (tick == 20) system.SetGlobalParameter("Intensity", 1f);
            if (tick == 60) loop.SetParameter("Intensity", 0.2f, immediate: true);
            if (tick == 30) duck = system.Mixer.StartSnapshot("Duck");
            if (tick == 50 && duck is not null) duck.Intensity = 0.5f;
            if (tick == 80) duck?.Stop();
            if (tick == 40)
            {
                vo = system.GetEvent("VO/Line").CreateInstance();
                vo.Callback += (_, e) => { if (e is ProgrammerSoundCallback p) p.Asset = new AssetRef("res://vo.ogg"); };
                vo.Start();
            }
            if (tick == 70) vo?.Stop(StopMode.AllowFadeout);
            if (tick == 90) system.Mixer.SetBusVolume("SFX", -3f);
            if (tick == 95) system.Mixer.SetBusMute("Music", true);
            if (tick == 100) system.Mixer.SetBusMute("Music", false);
            if (tick == 110) loop.SetPaused(true);
            if (tick == 120) loop.SetPaused(false);
            if (tick == 130) loop.TimelinePosition = 0.75;
            if (tick == 140)
            {
                EventInstance u = system.GetEvent("SFX/Unknown").CreateInstance();
                u.Callback += (_, e) => { if (e is SoundPlayedCallback sp) unknownVoice = sp.Voice; };
                u.Start();
                u.Release();
            }
            if (tick == 150 && unknownVoice.HasValue) system.Feedback(new VoiceFinished(unknownVoice.Value));
            if (tick == 180) system.StopAll(StopMode.AllowFadeout);
            system.Update(0.05);
            system.Commands.Clear();
        }
        return recorder.Stop();
    }

    [Test]
    public void Replay_SameSeed_HasNoDivergence()
    {
        AudioProject project = SessionProject();
        Recording recording = RunSession(project, 7);
        Assert.That(recording.Ticks, Has.Count.EqualTo(200));
        Assert.That(recording.Ticks[0].Calls, Has.Some.Matches<ApiCall>(c => c.Method == "CreateInstance" && c.Name == "Music/Loop"));
        Assert.That(recording.Ticks[40].ProgrammerSounds, Has.Count.EqualTo(1));
        Assert.That(recording.Ticks[40].ProgrammerSounds[0].Asset, Is.EqualTo("res://vo.ogg"));

        Recording replay = Replayer.Replay(recording, project);
        Divergence? divergence = Replayer.Compare(recording, replay);
        Assert.That(divergence, Is.Null, divergence?.ToString());
        int commands = 0;
        foreach (RecordedTick tick in recording.Ticks) commands += tick.Commands.Count;
        Assert.That(commands, Is.GreaterThan(100), "the session is non-trivial");
        int ended = 0;
        foreach (RecordedTick tick in recording.Ticks) ended += tick.Ended.Count;
        Assert.That(ended, Is.GreaterThanOrEqualTo(29 + 1 + 1), "every one-shot shot, the VO line and the snapshot report an end");
        int loopEndedAt = recording.Ticks.FindIndex(t => t.Ended.Contains(0));
        Assert.That(loopEndedAt, Is.EqualTo(180), "the persistent loop (handle 0) ends with StopAll at tick 180");
    }

    [Test]
    public void Replay_DifferentSeed_Diverges()
    {
        AudioProject project = SessionProject();
        Recording recording = RunSession(project, 7);
        Recording replay = Replayer.Replay(recording, project, seed: 8);
        Divergence? divergence = Replayer.Compare(recording, replay);
        Assert.That(divergence, Is.Not.Null);
        Assert.That(divergence!.Value.Expected, Does.StartWith("VoicePlay"));
        Assert.That(divergence.Value.Expected, Is.Not.EqualTo(divergence.Value.Actual));
    }

    [Test]
    public void RecordingJson_RoundTrip_ReplaysIdentically()
    {
        AudioProject project = SessionProject();
        Recording recording = RunSession(project, 3);
        string json = RecordingJson.Serialize(recording);
        Recording loaded = RecordingJson.Deserialize(json);
        Assert.That(loaded.Seed, Is.EqualTo(3));
        Assert.That(loaded.Locale, Is.EqualTo("en"));
        Assert.That(loaded.Ticks, Has.Count.EqualTo(200));
        Assert.That(RecordingJson.Serialize(loaded), Is.EqualTo(json));
        Assert.That(Replayer.Compare(recording, loaded), Is.Null);
        Assert.That(Replayer.Compare(recording, Replayer.Replay(loaded, project)), Is.Null);
    }

    [Test]
    public void SpatialRegions_RoundTripExactly_AndReplayRegionChanges()
    {
        AudioProject project = SessionProject();
        var system = new DirgeSystem(project, 17);
        var recorder = new Recorder(system);
        // Above float's exact integer range: region IDs must never be packed into the coordinate vector.
        var a = new SpatialRegionId(16777217);
        var b = new SpatialRegionId(int.MaxValue);
        system.SetListener(0, Attributes3D.Origin with { Region = a });
        system.SetListener(1, Attributes3D.Origin with { Region = b });
        EventInstance shot = system.GetEvent("SFX/Shot").CreateInstance();
        shot.Set3DAttributes(Attributes3D.Origin with { Region = a });
        shot.Start();
        Tick(system);
        shot.Set3DAttributes(Attributes3D.Origin with { Region = b });
        Tick(system);
        Recording recording = recorder.Stop();
        Recording loaded = RecordingJson.Deserialize(RecordingJson.Serialize(recording));
        Assert.That(loaded.Ticks[0].Calls.Find(c => c.Method == "Set3DAttributes")!.Region, Is.EqualTo(a.Value));
        Assert.That(loaded.Ticks[1].Calls.Find(c => c.Method == "Set3DAttributes")!.Region, Is.EqualTo(b.Value));
        Assert.That(Replayer.Compare(recording, Replayer.Replay(loaded, project)), Is.Null);
        loaded.Ticks[1].Calls.Find(c => c.Method == "Set3DAttributes")!.Region = a.Value;
        Assert.That(Replayer.Compare(recording, Replayer.Replay(loaded, project)), Is.Not.Null);
    }

    [Test]
    public void LegacyRecordingWithoutRegion_LoadsAsZero_AndKeepsCommandText()
    {
        Recording loaded = RecordingJson.Deserialize(
            """{"ticks":[{"calls":[{"method":"SetListener","vector":[0,0,0,0,0,0,0,0,-1,0,1,0]}]}]}""");
        Assert.That(loaded.Ticks[0].Calls[0].Region, Is.Zero);
        Assert.That(RecordingJson.Serialize(loaded), Does.Not.Contain("\"region\""));
        var legacy = new Attributes3D(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -1), Vector3.UnitY);
        Assert.That(CommandText.Format(new VoiceSet3D(new VoiceId(4), legacy)),
            Is.EqualTo("VoiceSet3D(4, p=[0 0 0] v=[0 0 0] f=[0 0 -1] u=[0 1 0])"));
    }

    [Test]
    public void Recorder_IgnoresInternalCalls_AndDetachesOnStop()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("A");
        ev.Macros.MaxInstances = 1;
        b.AddClip(b.AddTrack(ev), "res://a.wav");
        AudioEvent cmd = b.AddEvent("B");
        b.AddInstrument(b.AddTrack(cmd), new CommandInstrument { Action = CommandAction.StartEvent, TargetEvent = ev.Id });
        b.Project.AssetLengths["res://a.wav"] = 5.0;
        var system = new DirgeSystem(b.Build());
        var recorder = new Recorder(system);

        EventInstance first = system.GetEvent("A").CreateInstance();
        first.Start();
        Tick(system);
        EventInstance second = system.GetEvent("A").CreateInstance();
        second.Start(); // steals: victim.Stop must not be recorded
        Tick(system);
        EventInstance starter = system.GetEvent("B").CreateInstance();
        starter.Start(); // command instrument creates+starts an A internally during Update
        Tick(system);
        Recording recording = recorder.Stop();

        var methods = new List<string>();
        foreach (RecordedTick tick in recording.Ticks) foreach (ApiCall call in tick.Calls) methods.Add(call.Method);
        Assert.That(methods, Is.EqualTo(new[] { "CreateInstance", "Start", "CreateInstance", "Start", "CreateInstance", "Start" }));
        Assert.That(system.Recorder, Is.Null);
        Assert.That(system.InstanceCount, Is.EqualTo(4), "command-started instance exists but was not recorded as an API call");

        first.Stop(StopMode.Immediate);
        system.Update(0.1);
        Assert.That(recording.Ticks, Has.Count.EqualTo(3), "nothing recorded after Stop()");
        Assert.That(Replayer.Compare(recording, Replayer.Replay(recording, b.Project)), Is.Null);
    }

    /// <summary>What a driver does: apply (here: drop) and clear the commands.</summary>
    private static void Tick(DirgeSystem system)
    {
        system.Update(0.1);
        system.Commands.Clear();
    }

    [Test]
    public void Compare_ReportsCountMismatches()
    {
        var a = new Recording();
        a.Ticks.Add(new RecordedTick());
        a.Ticks[0].Commands.Add("X");
        var bRec = new Recording();
        bRec.Ticks.Add(new RecordedTick());
        Assert.That(Replayer.Compare(a, bRec), Is.EqualTo(new Divergence(0, 0, "X", "<no command>")));
        bRec.Ticks[0].Commands.Add("X");
        Assert.That(Replayer.Compare(a, bRec), Is.Null);
        bRec.Ticks.Add(new RecordedTick());
        Assert.That(Replayer.Compare(a, bRec)!.Value.Tick, Is.EqualTo(1));
    }
}
