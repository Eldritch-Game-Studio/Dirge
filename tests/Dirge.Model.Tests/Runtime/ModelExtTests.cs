using System.Collections.Generic;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Runtime;

[TestFixture]
public sealed class ModelExtTests
{
    private const double Tolerance = 1e-6;

    // ---- transition timelines ----------------------------------------------------------------------------------

    [Test]
    public void TransitionTimeline_PlaysTransitionInstrumentsForTransitionLength_ThenLandsAtDestination()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Music/Trans");
        Track track = b.AddTrack(ev);
        b.AddClip(track, "res://source.wav", 0f, 10f);
        b.Project.AssetLengths["res://source.wav"] = 10.0;
        b.Project.AssetLengths["res://riser.wav"] = 5.0;
        DestinationMarker dest = b.AddMarker(ev, new DestinationMarker(), 8f, "Dest");
        TransitionMarker transition = b.AddMarker(ev, new TransitionMarker { Destination = dest.Id, TransitionLength = 1f }, 2f, "Go");
        transition.TransitionInstruments.Add(new ClipInstrument
        {
            Id = b.NextId(), Name = "Riser", Asset = new AssetRef("res://riser.wav"),
            Placement = new Placement { Parameter = DirgeId.None, Start = 0f, Length = 0f },
        });

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Music/Trans");
        instance.Start();
        h.Step(0.1, 19);
        Assert.That(instance.IsInTransition, Is.False);
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1), "only the source clip before the marker");

        int transitionTicks = 0;
        for (int i = 0; i < 40; i++)
        {
            h.Step(0.1);
            if (instance.IsInTransition) transitionTicks++;
            else if (transitionTicks > 0) break;
        }
        Assert.That(transitionTicks, Is.EqualTo(10), "transition timeline runs for exactly TransitionLength");

        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(3));
        Assert.That(plays[1].Asset.Path, Is.EqualTo("res://riser.wav"), "transition instrument fires at transition start");
        Assert.That(plays[1].StartSeconds, Is.EqualTo(0).Within(1e-3));
        Assert.That(plays[2].Asset.Path, Is.EqualTo("res://source.wav"), "destination re-enters the source clip");
        Assert.That(plays[2].StartSeconds, Is.EqualTo(8.0).Within(1e-3), "lands at the destination marker");
        Assert.That(h.Count<VoiceStop>(), Is.EqualTo(2), "source cut at transition start, riser cut at transition end");
        Assert.That(instance.TimelinePosition, Is.GreaterThanOrEqualTo(8.0).And.LessThan(8.2));
    }

    [Test]
    public void TransitionWithoutLength_JumpsImmediately()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Music/Jump");
        ev.Macros.Persistent = true;
        DestinationMarker dest = b.AddMarker(ev, new DestinationMarker(), 5f, "Dest");
        b.AddMarker(ev, new TransitionMarker { Destination = dest.Id }, 1f, "Go");

        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Music/Jump");
        instance.Start();
        h.Step(0.5, 3);
        Assert.That(instance.IsInTransition, Is.False);
        Assert.That(instance.TimelinePosition, Is.EqualTo(5.5).Within(Tolerance));
    }

    // ---- start quantization / conductor -------------------------------------------------------------------------

    private static ProjectBuilder ConductorProject()
    {
        var b = new ProjectBuilder();
        AudioEvent music = b.AddEvent("Music/Main");
        music.IsConductor = true;
        music.Macros.Persistent = true;
        b.AddTempo(music, 120f, 0f, 4); // bar = 2 s
        b.AddInstrument(b.AddTrack(music), new ClipInstrument { Asset = new AssetRef("res://loop.wav"), Loop = true });
        AudioEvent stinger = b.AddEvent("Music/Stinger");
        stinger.Macros.StartQuantization = Quantization.Bar;
        b.AddClip(b.AddTrack(stinger), "res://hit.wav");
        b.Project.AssetLengths["res://hit.wav"] = 1.0;
        b.Project.AssetLengths["res://loop.wav"] = 4.0;
        return b;
    }

    [Test]
    public void StartQuantization_Bar_DefersToConductorsNextBar()
    {
        var h = new Harness(ConductorProject().Build());
        EventInstance music = h.Create("Music/Main");
        music.Start();
        h.Step(0.125, 4); // conductor at 0.5 s
        Assert.That(h.System.Conductor, Is.SameAs(music));

        EventInstance stinger = h.Create("Music/Stinger");
        stinger.Start();
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Starting));
        h.Step(0.125, 11); // 1.375 s later: still before the bar at 2.0
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Starting));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));
        h.Step(0.125); // conductor reaches 2.0
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(2));
        Assert.That(music.TimelinePosition, Is.EqualTo(2.0).Within(Tolerance));
    }

    [Test]
    public void StartQuantization_WithoutConductor_StartsImmediately()
    {
        var h = new Harness(ConductorProject().Build());
        EventInstance stinger = h.Create("Music/Stinger");
        stinger.Start();
        h.Step(0.1);
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(h.Count<VoicePlay>(), Is.EqualTo(1));
    }

    [Test]
    public void StartQuantization_ScheduleLookahead_SkipsGridPointsTooClose()
    {
        ProjectBuilder b = ConductorProject();
        b.Project.Events[1].Macros.StartQuantization = Quantization.Beat; // beat = 0.5 s
        b.Project.Events[1].Macros.ScheduleLookahead = 0.3f;
        var h = new Harness(b.Build());
        EventInstance music = h.Create("Music/Main");
        music.Start();
        h.Step(0.125, 3); // 0.375 s: next beat at 0.5 is only 0.125 away (< lookahead) → use 1.0
        EventInstance stinger = h.Create("Music/Stinger");
        stinger.Start();
        h.Step(0.125, 4); // 0.875
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Starting));
        h.Step(0.125); // 1.0
        Assert.That(stinger.State, Is.EqualTo(PlaybackState.Playing));
    }

    [Test]
    public void Conductor_ClearsWhenItStops()
    {
        var h = new Harness(ConductorProject().Build());
        EventInstance music = h.Create("Music/Main");
        music.Start();
        h.Step(0.1);
        Assert.That(h.System.Conductor, Is.SameAs(music));
        music.Stop(StopMode.Immediate);
        Assert.That(h.System.Conductor, Is.Null);
    }

    // ---- parameter velocity -------------------------------------------------------------------------------------

    [Test]
    public void ParameterVelocity_EqualsRateOfChange()
    {
        var b = new ProjectBuilder();
        ParameterDefinition p = b.AddGlobalParameter("Speed", 0f, 10f);
        p.SeekSpeedUp = 2f;
        AudioEvent ev = b.AddEvent("Test/V");
        ev.Macros.Persistent = true;
        ParameterDefinition local = b.AddLocalParameter(ev, "Local", 0f, 10f);
        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/V");
        instance.Start();
        h.Step(0.1);

        h.System.SetGlobalParameter("Speed", 5f);
        h.Step(0.1);
        Assert.That(h.System.GetGlobalParameter("Speed"), Is.EqualTo(0.2f).Within(1e-5f));
        Assert.That(instance.GetParameterVelocity("Speed"), Is.EqualTo(2f).Within(1e-4f), "seek speed is the velocity");

        instance.SetParameter("Local", 0.5f, immediate: true);
        h.Step(0.1);
        Assert.That(instance.GetParameterVelocity("Local"), Is.EqualTo(5f).Within(1e-4f), "immediate set over one 0.1 s tick");
        h.Step(0.1);
        Assert.That(instance.GetParameterVelocity("Local"), Is.EqualTo(0f).Within(1e-6f), "steady afterwards");
        Assert.That(instance.GetParameterVelocity("Nope"), Is.EqualTo(0f));
        _ = local;
    }

    // ---- magnet regions -----------------------------------------------------------------------------------------

    [Test]
    public void MagnetRegion_SnapsSeeksToItsPosition()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Test/Magnet");
        ev.Macros.Persistent = true;
        b.AddMarker(ev, new MagnetRegion { Length = 2f }, 5f, "Snap");
        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Magnet");
        instance.Start();
        h.Step(0.1);

        instance.TimelinePosition = 6.0;
        h.Step(0.1);
        Assert.That(instance.TimelinePosition, Is.EqualTo(5.1).Within(Tolerance), "inside the region → snapped to 5 then advanced");

        instance.TimelinePosition = 7.5;
        h.Step(0.1);
        Assert.That(instance.TimelinePosition, Is.EqualTo(7.6).Within(Tolerance), "outside the region → untouched");

        EventInstance second = h.Create("Test/Magnet");
        second.TimelinePosition = 5.5;
        second.Start();
        h.Step(0.1);
        Assert.That(second.TimelinePosition, Is.EqualTo(5.1).Within(Tolerance), "initial position is a seek too");
    }

    // ---- audio tables -------------------------------------------------------------------------------------------

    private static ProjectBuilder TableProject()
    {
        var b = new ProjectBuilder();
        AudioEvent ev = b.AddEvent("Dialogue/Line");
        b.AddInstrument(b.AddTrack(ev), new ProgrammerInstrument { Key = "greeting" });
        b.AddInstrument(ev.Tracks[0], new ProgrammerInstrument { Key = "untabled" }, 0.5f);
        var table = new AudioTable { Id = b.NextId(), Name = "Dialogue" };
        table.Entries["greeting"] = new Dictionary<string, AssetRef>
        {
            ["en"] = new AssetRef("res://en/greeting.wav"),
            ["fr"] = new AssetRef("res://fr/greeting.wav"),
        };
        b.Project.AudioTables.Add(table);
        foreach (string a in new[] { "res://en/greeting.wav", "res://fr/greeting.wav", "res://cb.wav", "res://fallback.wav" })
            b.Project.AssetLengths[a] = 1.0;
        return b;
    }

    [Test]
    public void AudioTable_ResolvesByLocale_FallingBackToFirstLocale()
    {
        var h = new Harness(TableProject().Build());
        h.System.Locale = "fr";
        EventInstance instance = h.Create("Dialogue/Line");
        instance.Start();
        h.Step(0.1);
        Assert.That(h.All<VoicePlay>()[0].Asset.Path, Is.EqualTo("res://fr/greeting.wav"));

        var h2 = new Harness(TableProject().Build());
        h2.System.Locale = "de";
        EventInstance other = h2.Create("Dialogue/Line");
        other.Start();
        h2.Step(0.1);
        Assert.That(h2.All<VoicePlay>()[0].Asset.Path, Is.EqualTo("res://en/greeting.wav"), "unknown locale → first authored locale");
    }

    [Test]
    public void ProgrammerResolution_TableThenCallbackThenSetProgrammerSound()
    {
        var h = new Harness(TableProject().Build());
        h.System.Locale = "en";
        EventInstance instance = h.Create("Dialogue/Line");
        var keys = new List<string>();
        instance.Callback += (_, e) =>
        {
            if (e is ProgrammerSoundCallback p) { keys.Add(p.Key); if (p.Key == "untabled") p.Asset = new AssetRef("res://cb.wav"); }
        };
        instance.SetProgrammerSound(new AssetRef("res://fallback.wav"));
        instance.Start();
        h.Step(0.1, 7);
        List<VoicePlay> plays = h.All<VoicePlay>();
        Assert.That(plays, Has.Count.EqualTo(2));
        Assert.That(plays[0].Asset.Path, Is.EqualTo("res://en/greeting.wav"));
        Assert.That(plays[1].Asset.Path, Is.EqualTo("res://cb.wav"));
        Assert.That(keys, Is.EqualTo(new[] { "untabled" }), "the callback is only asked when tables miss");

        var h2 = new Harness(TableProject().Build());
        EventInstance quiet = h2.Create("Dialogue/Line");
        quiet.CallbackMask = CallbackMask.None;
        quiet.SetProgrammerSound(new AssetRef("res://fallback.wav"));
        quiet.Start();
        h2.Step(0.1, 7);
        Assert.That(h2.All<VoicePlay>()[1].Asset.Path, Is.EqualTo("res://fallback.wav"));
    }

    // ---- platform overrides -------------------------------------------------------------------------------------

    [Test]
    public void PlatformOverride_ReplacesAuthoredBaselineForMatchingPlatform()
    {
        var b = new ProjectBuilder();
        Bus music = b.AddBus("Music", volumeDb: -3f);
        var po = new PlatformOverride { Platform = "Linux" };
        po.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(music.Id), Value = -9f });
        b.Project.PlatformOverrides.Add(po);
        AudioProject project = b.Build();

        var linux = new DirgeSystem(project, 1, platform: "linux");
        linux.Update(0.1);
        Assert.That(linux.Platform, Is.EqualTo("linux"));
        Assert.That(linux.Mixer.EffectiveBusVolumeDb(music.Id), Is.EqualTo(-9f).Within(1e-5f), "case-insensitive match applies");
        Assert.That(linux.Commands.Commands, Has.Some.Matches<Command>(c => c is BusSetVolume { Bus: "Music", VolumeDb: -9f }));

        var other = new DirgeSystem(project, 1, platform: "Windows");
        other.Update(0.1);
        Assert.That(other.Mixer.EffectiveBusVolumeDb(music.Id), Is.EqualTo(-3f).Within(1e-5f));

        var none = new DirgeSystem(project, 1);
        none.Update(0.1);
        Assert.That(none.Mixer.EffectiveBusVolumeDb(music.Id), Is.EqualTo(-3f).Within(1e-5f));
    }

    // ---- mixer automation ---------------------------------------------------------------------------------------

    [Test]
    public void MixerAutomation_LaneDrivesBusVolumeFromGlobalParameter()
    {
        var b = new ProjectBuilder();
        Bus music = b.AddBus("Music");
        ParameterDefinition intensity = b.AddGlobalParameter("Intensity");
        var lane = new AutomationLane { Id = b.NextId(), Target = PropertyRef.BusVolume(music.Id), Parameter = intensity.Id };
        lane.Curve.Points.Add(new CurvePoint(0f, 0f));
        lane.Curve.Points.Add(new CurvePoint(1f, -12f));
        b.Project.MixerAutomation.Add(lane);

        var h = new Harness(b.Build());
        h.Step(0.1);
        Assert.That(h.System.Mixer.EffectiveBusVolumeDb(music.Id), Is.EqualTo(0f).Within(1e-5f));
        h.System.SetGlobalParameter("Intensity", 0.5f, immediate: true);
        h.Step(0.1);
        Assert.That(h.System.Mixer.EffectiveBusVolumeDb(music.Id), Is.EqualTo(-6f).Within(1e-4f));
        Assert.That(h.All<BusSetVolume>().FindLast(c => c.Bus == "Music")!.VolumeDb, Is.EqualTo(-6f).Within(1e-4f));
    }

    // ---- sync groups --------------------------------------------------------------------------------------------

    [Test]
    public void SyncGroup_IsForwardedOnVoicePlay()
    {
        var b = ProjectBuilder.Minimal();
        b.Project.Events[0].Tracks[0].Instruments[0].SyncGroup = 3;
        var h = new Harness(b.Build());
        EventInstance instance = h.Create("Test/Beep");
        instance.Start();
        h.Step(0.1);
        Assert.That(h.All<VoicePlay>()[0].SyncGroup, Is.EqualTo(3));
    }

    // ---- instances list -----------------------------------------------------------------------------------------

    [Test]
    public void Instances_ListsTopLevelLiveInstances()
    {
        var h = new Harness(ProjectBuilder.Minimal().Build());
        EventInstance instance = h.Create("Test/Beep");
        Assert.That(h.System.Instances, Has.Count.EqualTo(1).And.Contains(instance));
        instance.Release();
        h.Step(0.1);
        Assert.That(h.System.Instances, Is.Empty);
    }
}
