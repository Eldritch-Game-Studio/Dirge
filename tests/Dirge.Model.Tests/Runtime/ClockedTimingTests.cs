using System.Linq;
using Dirge.Profiling;
using Dirge.Project;
using Dirge.Runtime;
using NUnit.Framework;

namespace Dirge.Tests.Runtime;

[TestFixture]
public sealed class ClockedTimingTests
{
    private static AudioProject TimingProject()
    {
        var b = new ProjectBuilder();
        foreach (string path in new[] { "Music/First", "Music/Second" })
        {
            AudioEvent ev = b.AddEvent(path);
            string asset = "res://" + path + ".ogg";
            b.AddClip(b.AddTrack(ev), asset);
            b.Project.AssetLengths[asset] = 180;
        }
        return b.Build();
    }

    [Test]
    public void StartAndSeekExcludeTimeBeforeMutation()
    {
        const double beforeMutation = 1.39733;
        double elapsed = 0;
        var system = new DirgeSystem(TimingProject(), elapsedSinceLastUpdate: () => elapsed);
        var running = system.GetEvent("Music/Second").CreateInstance();
        running.Start();
        system.Update(0);
        system.Commands.Clear();

        elapsed = beforeMutation;
        var music = system.GetEvent("Music/First").CreateInstance();
        double callbackPosition = -1;
        music.Callback += (owner, callback) =>
        {
            if (callback is SoundPlayedCallback) callbackPosition = owner.TimelinePosition;
        };
        music.Start();
        music.TimelinePosition = 0.27;
        const double afterMutation = 0.025;
        system.Update(beforeMutation + afterMutation);

        Assert.That(music.TimelinePosition, Is.EqualTo(0.295).Within(1e-9));
        Assert.That(callbackPosition, Is.EqualTo(0.295).Within(1e-9), "The first voice callback sees the corrected position.");
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Single().StartSeconds,
            Is.EqualTo(0.295).Within(1e-9), "The native onset must match the model counter.");
        Assert.That(running.TimelinePosition, Is.EqualTo(beforeMutation + afterMutation).Within(1e-9));

        system.Commands.Clear();
        system.Update(0.1);
        Assert.That(music.TimelinePosition, Is.EqualTo(0.395).Within(1e-9), "The mutation offset is consumed exactly once.");
        Assert.That(system.Commands.Commands.OfType<VoiceSeek>(), Is.Empty);
    }

    [Test]
    public void LatestSeekUsesItsOwnBoundary_AndEmitsPostBoundaryPosition()
    {
        double elapsed = 0;
        var system = new DirgeSystem(TimingProject(), elapsedSinceLastUpdate: () => elapsed);
        var music = system.GetEvent("Music/First").CreateInstance();
        var running = system.GetEvent("Music/Second").CreateInstance();
        music.Start();
        running.Start();
        system.Update(0);
        system.Commands.Clear();

        elapsed = 1.4;
        music.TimelinePosition = 125;
        elapsed = 1.5;
        music.TimelinePosition = 126;
        system.Update(1.525);

        Assert.That(music.TimelinePosition, Is.EqualTo(126.025).Within(1e-9));
        Assert.That(running.TimelinePosition, Is.EqualTo(1.525).Within(1e-9));
        Assert.That(system.Commands.Commands.OfType<VoiceSeek>().Single().Seconds,
            Is.EqualTo(126.025).Within(1e-9), "A playing voice includes time after, but not before, the latest seek.");
        Assert.That(system.Commands.Commands.OfType<VoicePlay>(), Is.Empty, "A seek preserves the tracked native voice.");

        system.Commands.Clear();
        system.Update(0.1);
        Assert.That(music.TimelinePosition, Is.EqualTo(126.125).Within(1e-9));
        Assert.That(system.Commands.Commands.OfType<VoiceSeek>(), Is.Empty);
    }

    [TestCase(0.0)]
    [TestCase(10.0)]
    public void ShortCue_IsNotDroppedByPreStartTime_AndStillFinishes(double beforeMutation)
    {
        double elapsed = beforeMutation;
        var project = ProjectBuilder.Minimal("SFX/Click", assetLength: 0.05).Build();
        var system = new DirgeSystem(project, elapsedSinceLastUpdate: () => elapsed);
        var cue = system.GetEvent("SFX/Click").CreateInstance();
        cue.Start();
        cue.Release();
        // Zero models the first server tick after loading/resetting its clock. The long
        // interval models a running-frame stall that happened entirely before this cue.
        system.Update(beforeMutation);
        Assert.That(cue.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Single().StartSeconds, Is.Zero);
        Assert.That(system.Commands.Commands.OfType<VoiceStop>(), Is.Empty);
        Assert.That(cue.TimelinePosition, Is.Zero);

        system.Commands.Clear();
        system.Update(cue.Description.Length + 1);
        Assert.That(cue.State, Is.EqualTo(PlaybackState.Stopped));
        Assert.That(system.InstanceCount, Is.Zero, "Auto-release returns the completed cue to its pool.");
    }

    [Test]
    public void RecorderReplaysMutationOffsetsWithoutLiveClock()
    {
        double elapsed = 0;
        var project = TimingProject();
        var system = new DirgeSystem(project, elapsedSinceLastUpdate: () => elapsed);
        var recorder = new Recorder(system);
        system.Update(0);
        system.Commands.Clear();
        elapsed = 1.4;
        var music = system.GetEvent("Music/First").CreateInstance();
        music.Start();
        music.TimelinePosition = 10;
        system.Update(1.425);
        system.Commands.Clear();
        elapsed = 0.5;
        music.TimelinePosition = 30;
        system.Update(0.525);
        system.Commands.Clear();
        music.Stop(StopMode.Immediate);
        music.Release();
        system.Update(0);
        var recording = recorder.Stop();
        var loaded = RecordingJson.Deserialize(RecordingJson.Serialize(recording));
        Assert.That(Replayer.Compare(recording, Replayer.Replay(loaded, project)), Is.Null);
    }

    [Test]
    public void InternalStartsStayOnModelTick_NotDriverMutationClock()
    {
        double elapsed = 0;
        var system = new DirgeSystem(TimingProject(), elapsedSinceLastUpdate: () => elapsed);
        var first = system.GetEvent("Music/First").CreateInstance();
        var second = system.GetEvent("Music/Second").CreateInstance();
        first.Callback += (_, callback) => { if (callback is StartedCallback) second.Start(); };
        first.Start();
        elapsed = 10; // An internal start must ignore this between-tick driver clock.
        system.Update(0.25);
        Assert.That(second.TimelinePosition, Is.EqualTo(0.25).Within(1e-9));
    }

    [Test]
    public void UnclockedSystem_RetainsDeterministicTickBoundarySemantics()
    {
        var system = new DirgeSystem(TimingProject());
        var music = system.GetEvent("Music/First").CreateInstance();
        music.Start();
        music.TimelinePosition = 10;
        system.Update(0.25);
        Assert.That(music.TimelinePosition, Is.EqualTo(10.25).Within(1e-9));
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Single().StartSeconds, Is.EqualTo(10));
    }

    [TestCase(0.75, 0.0f, 1.0)] // The next grid is still ahead of the mutation.
    [TestCase(1.25, 0.0f, 2.0)] // The old model tick's next grid has already elapsed.
    [TestCase(0.75, 0.5f, 2.0)] // Lookahead starts at the mutation, not the old tick.
    public void QuantizedStart_UsesMutationGrid_AndRetainsBoundaryWhileWaiting(
        double beforeMutation, float lookahead, double grid)
    {
        var project = TimingProject();
        var conductorEvent = project.Events.Single(ev => ev.Path == "Music/Second");
        conductorEvent.IsConductor = true;
        conductorEvent.Tempo.Add(new TempoMarker { Position = 0, Bpm = 60 });
        var musicEvent = project.Events.Single(ev => ev.Path == "Music/First");
        musicEvent.Macros.StartQuantization = Quantization.Beat;
        musicEvent.Macros.ScheduleLookahead = lookahead;
        double elapsed = 0;
        var system = new DirgeSystem(project, elapsedSinceLastUpdate: () => elapsed);
        var recorder = new Recorder(system);
        var conductor = system.GetEvent(conductorEvent.Path).CreateInstance();
        conductor.Start();
        system.Update(0);
        system.Commands.Clear();

        elapsed = beforeMutation;
        var music = system.GetEvent(musicEvent.Path).CreateInstance();
        music.Start();
        double firstTick = beforeMutation + 0.1;
        system.Update(firstTick);
        Assert.That(music.State, Is.EqualTo(PlaybackState.Starting));
        Assert.That(system.Commands.Commands.OfType<VoicePlay>(), Is.Empty);
        system.Commands.Clear();

        system.Update(grid + 0.1 - firstTick);
        Assert.That(conductor.TimelinePosition, Is.EqualTo(grid + 0.1).Within(1e-9));
        Assert.That(music.State, Is.EqualTo(PlaybackState.Playing));
        Assert.That(music.TimelinePosition, Is.EqualTo(0.1).Within(1e-9));
        Assert.That(system.Commands.Commands.OfType<VoicePlay>().Single().StartSeconds,
            Is.EqualTo(0.1).Within(1e-9), "The native onset includes only time after the selected grid.");
        var recording = recorder.Stop();
        Assert.That(Replayer.Compare(recording, Replayer.Replay(recording, project)), Is.Null);
    }

    [Test]
    public void Seek_TransitionRegionJumpInBegin_DefersNativeSeekUntilAfterAdvance()
    {
        var project = TimingProject();
        var audioEvent = project.Events.Single(ev => ev.Path == "Music/First");
        var destination = new DestinationMarker { Name = "AfterJump", Position = 50 };
        audioEvent.Markers.Add(destination);
        audioEvent.Markers.Add(new TransitionRegion
        {
            Position = 10, Length = 2, Destination = destination.Id,
            Quantization = Quantization.None,
        });
        double elapsed = 0;
        var system = new DirgeSystem(project, elapsedSinceLastUpdate: () => elapsed);
        var music = system.GetEvent(audioEvent.Path).CreateInstance();
        music.Start();
        system.Update(0);
        system.Commands.Clear();

        elapsed = 1.4;
        music.TimelinePosition = 10.5;
        system.Update(1.425);
        Assert.That(music.TimelinePosition, Is.EqualTo(50.025).Within(1e-9));
        Assert.That(system.Commands.Commands.OfType<VoiceSeek>().Single().Seconds,
            Is.EqualTo(50.025).Within(1e-9), "Begin's immediate jump must not emit the pre-advance destination.");
        Assert.That(system.Commands.Commands.OfType<VoicePlay>(), Is.Empty);
    }

    [TestCase(0.0210284)]
    [TestCase(0.125)]
    public void QuantizedUnclockedStart_EmitsNativeSampleAtTheConductorPhase(double overshoot)
    {
        var project = TimingProject();
        var conductorEvent = project.Events.Single(ev => ev.Path == "Music/Second");
        conductorEvent.IsConductor = true;
        conductorEvent.Tempo.Add(new TempoMarker { Position = 0, Bpm = 120 });
        project.Events.Single(ev => ev.Path == "Music/First").Macros.StartQuantization = Quantization.Beat;
        var system = new DirgeSystem(project);
        var recorder = new Recorder(system);
        var conductor = system.GetEvent("Music/Second").CreateInstance();
        conductor.Start();
        system.Update(0);
        system.Commands.Clear();
        system.Update(0.49);
        system.Commands.Clear();
        var cue = system.GetEvent("Music/First").CreateInstance();
        cue.Start();
        system.Update(0.01 + overshoot);

        var play = system.Commands.Commands.OfType<VoicePlay>().Single();
        Assert.That(cue.TimelinePosition, Is.EqualTo(overshoot).Within(1e-9));
        Assert.That(play.StartSeconds, Is.EqualTo(conductor.TimelinePosition % 0.5).Within(1e-9),
            "Internal/model-clock quantized starts must not lose the part of the frame after the beat.");
        var recording = recorder.Stop();
        Assert.That(Replayer.Compare(recording, Replayer.Replay(recording, project)), Is.Null);
    }

    [TestCase(0.0210284)]
    [TestCase(0.125)]
    public void TransitionJump_EmitsNewNativeLayerAtTheDestinationPhase(double overshoot)
    {
        var b = new ProjectBuilder();
        var ev = b.AddEvent("Music/Transition");
        b.AddClip(b.AddTrack(ev), "res://calm.wav", 0, 4);
        b.AddClip(b.AddTrack(ev), "res://combat.wav", 4, 4);
        b.Project.AssetLengths["res://calm.wav"] = 4;
        b.Project.AssetLengths["res://combat.wav"] = 4;
        var destination = b.AddMarker(ev, new DestinationMarker(), 4);
        b.AddMarker(ev, new TransitionMarker { Destination = destination.Id }, 0.5f);
        var project = b.Build();
        var system = new DirgeSystem(project);
        var recorder = new Recorder(system);
        var music = system.GetEvent(ev.Path).CreateInstance();
        music.Start();
        system.Update(0);
        system.Commands.Clear();
        system.Update(0.5 + overshoot);

        var play = system.Commands.Commands.OfType<VoicePlay>().Single(p => p.Asset.Path == "res://combat.wav");
        Assert.That(music.TimelinePosition, Is.EqualTo(4 + overshoot).Within(1e-9));
        Assert.That(play.StartSeconds, Is.EqualTo(overshoot).Within(1e-9),
            "The layer entered by a jump must include the remaining frame in its native sample offset.");
        var recording = recorder.Stop();
        Assert.That(Replayer.Compare(recording, Replayer.Replay(recording, project)), Is.Null);
    }
}
