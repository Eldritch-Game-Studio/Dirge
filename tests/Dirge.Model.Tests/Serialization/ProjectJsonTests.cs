using System.Text.Json;
using Dirge.Project;
using Dirge.Serialization;
using NUnit.Framework;

namespace Dirge.Tests.Serialization;

[TestFixture]
public sealed class ProjectJsonTests
{
    /// <summary>Every Instrument/Modulator/LogicMarker subtype, every value encoding, dictionaries, nested envelopes.</summary>
    private static AudioProject FullProject()
    {
        var b = new ProjectBuilder("Full");
        Bus music = b.AddBus("Music", volumeDb: -3f);
        Bus sfx = b.AddBus("SFX");
        BusEffect comp = b.AddEffect(music, "AudioEffectCompressor", ("threshold", -12f), ("ratio", 4f));
        comp.StringParameters["sidechain"] = "SFX";
        comp.Enabled = false;
        music.Mute = true;
        music.MaxVoices = 8;
        music.FaderIndex = 0;
        Vca vca = b.AddVca("MusicVca", -1.5f, music);

        ParameterDefinition intensity = b.AddGlobalParameter("Intensity", 0f, 1f, 0.25f);
        intensity.SeekSpeedUp = 2f;
        intensity.SeekSpeedDown = 0.5f;
        ParameterDefinition surface = b.AddGlobalParameter("Surface", 0f, 2f, 0f, ParameterKind.Labeled);
        surface.Labels.AddRange(new[] { "Grass", "Stone", "Water" });
        surface.Hold = false;
        ParameterDefinition distance = b.AddGlobalParameter("Distance", 0f, 50f);
        distance.BuiltIn = BuiltInParameter.Distance;

        Snapshot duck = b.AddSnapshot("Duck", priority: 5, mode: SnapshotMode.Overriding);
        duck.Intensity = new Ahdsr { Attack = 0.1f, Hold = 0.2f, Decay = 0.3f, Sustain = 0.8f, Release = 0.4f };
        duck.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(music.Id), Value = -12f });
        duck.Values.Add(new SnapshotValue { Target = PropertyRef.Effect(music.Id, 0, "threshold"), Value = -20f });
        duck.Values.Add(new SnapshotValue { Target = PropertyRef.VcaVolume(vca.Id), Value = -6f });

        AudioEvent child = b.AddEvent("Music/Child", music);
        b.AddClip(b.AddTrack(child), "res://child.ogg");

        AudioEvent ev = b.AddEvent("Music/Main", music);
        ev.Macros = new EventMacros
        {
            VolumeDb = -2f, Pitch = 1.1f, Priority = 32, MaxInstances = 3, Stealing = StealingMode.Quietest,
            Cooldown = 0.5f, Persistent = true, Is3D = true, MinDistance = 2f, MaxDistance = 40f, Rolloff = Rolloff.Logarithmic, Doppler = true,
            DopplerScale = 0.5f, ScheduleLookahead = 0.1f,
        };
        ParameterDefinition local = b.AddLocalParameter(ev, "Danger", 0f, 10f, 1f, ParameterKind.Discrete);

        Track track = b.AddTrack(ev, "Instruments", -1f);
        ClipInstrument clip = b.AddClip(track, "res://a.ogg", 0f, 4f);
        clip.VolumeRandomDb = 1f; clip.PitchRandom = 0.1f; clip.StartOffset = 0.25f; clip.FadeIn = 0.1f; clip.FadeOut = 0.2f;
        clip.Loop = true; clip.Async = true; clip.CutMode = CutMode.FadeOut; clip.Polyphony = 2; clip.TriggerDelay = 0.05f; clip.Probability = 0.9f;
        clip.Conditions.Add(new TriggerCondition { Parameter = local.Id, Minimum = 1f, Maximum = 5f });

        var multi = b.AddSheetInstrument(track, new MultiInstrument { Mode = PlaylistMode.Shuffle, AvoidRepeat = 2 }, intensity, 0f, 0.5f);
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://m1.ogg"), Weight = 2f });
        multi.Entries.Add(new MultiEntry { Asset = new AssetRef("res://m2.ogg") });

        var scatterer = b.AddInstrument(track, new ScattererInstrument { MinInterval = 0.2f, MaxInterval = 1f, MinScatterDistance = 1f, MaxScatterDistance = 5f, Mode = PlaylistMode.Sequential }, 1f, 10f);
        scatterer.Entries.Add(new MultiEntry { Asset = new AssetRef("res://s1.ogg") });

        b.AddInstrument(track, new SilenceInstrument(), 2f, 1f);
        b.AddInstrument(track, new EventInstrument { Event = child.Id }, 3f, 0f);
        b.AddInstrument(track, new ProgrammerInstrument { Key = "line_001" }, 4f, 0f);
        b.AddInstrument(track, new CommandInstrument { Action = CommandAction.SetParameter, TargetEvent = child.Id, TargetParameter = intensity.Id, Value = 0.7f }, 5f, 0f);
        b.AddInstrument(track, new SnapshotInstrument { Snapshot = duck.Id }, 6f, 2f);

        b.AddAutomation(ev, PropertyRef.EventVolume(ev.Id), intensity,
            new CurvePoint(0f, -20f, CurveShape.Smooth), new CurvePoint(0.5f, -6f, CurveShape.Stepped), new CurvePoint(1f, 0f));
        b.AddAutomation(ev, PropertyRef.InstrumentPitch(clip.Id), local, new CurvePoint(0f, 1f), new CurvePoint(10f, 2f));

        b.AddModulator(ev, new RandomModulator(), PropertyRef.EventPitch(ev.Id), 0.05f);
        b.AddModulator(ev, new AhdsrModulator { Envelope = new Ahdsr { Attack = 0.5f, Sustain = 0.6f, Release = 1f } }, PropertyRef.EventVolume(ev.Id), -12f);
        b.AddModulator(clip, new LfoModulator { Shape = LfoShape.Triangle, RateHz = 2f, Phase = 0.25f, TempoSync = true }, PropertyRef.InstrumentVolume(clip.Id), 3f);

        var dest = b.AddMarker(ev, new DestinationMarker(), 8f, "Verse");
        b.AddMarker(ev, new LoopRegion { Length = 4f }, 8f, "Loop");
        var transition = b.AddMarker(ev, new TransitionMarker { Destination = dest.Id, Quantization = Quantization.Bar, TransitionLength = 0.5f }, 12f, "ToVerse");
        transition.Conditions.Add(new TriggerCondition { Parameter = intensity.Id, Minimum = 0.5f, Maximum = 1f });
        transition.Probability = 0.75f;
        b.AddMarker(ev, new TransitionRegion { Destination = dest.Id, Length = 2f, Quantization = Quantization.Beat }, 14f, "Region");
        b.AddMarker(ev, new SustainPoint(), 16f, "Hold");
        b.AddTempo(ev, 128f, 0f, 4);
        b.AddTempo(ev, 96f, 8f, 3);

        b.AddBank("Main", ev, child);
        AudioProject project = b.Build();
        project.AssetLengths["res://a.ogg"] = 4.0;
        project.AssetLengths["res://child.ogg"] = 1.5;
        _ = sfx;
        return project;
    }

    /// <summary>NUnit 4.6 overloads TestDelegate/Action, so lambdas need an explicitly typed delegate.</summary>
    private static void AssertRejects(string json)
    {
        System.Action act = () => ProjectJson.Deserialize(json);
        Assert.Throws<JsonException>(act);
    }

    [Test]
    public void RoundTrip_FullProject_ReserializesIdentically()
    {
        AudioProject original = FullProject();
        string json = ProjectJson.Serialize(original);
        AudioProject copy = ProjectJson.Deserialize(json);
        string again = ProjectJson.Serialize(copy);
        Assert.That(again, Is.EqualTo(json));
    }

    [Test]
    public void RoundTrip_CompactAndIndented_AreEquivalent()
    {
        AudioProject original = FullProject();
        string compact = ProjectJson.Serialize(original, indented: false);
        Assert.That(compact, Does.Not.Contain("\n"));
        AudioProject copy = ProjectJson.Deserialize(compact);
        Assert.That(ProjectJson.Serialize(copy), Is.EqualTo(ProjectJson.Serialize(original)));
    }

    [Test]
    public void RoundTrip_PreservesPolymorphicTypesAndValues()
    {
        AudioProject copy = ProjectJson.Deserialize(ProjectJson.Serialize(FullProject()));
        AudioEvent ev = copy.Events.Find(e => e.Path == "Music/Main")!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev.Tracks[0].Instruments, Has.Count.EqualTo(8));
            Assert.That(ev.Tracks[0].Instruments[0], Is.TypeOf<ClipInstrument>());
            Assert.That(ev.Tracks[0].Instruments[1], Is.TypeOf<MultiInstrument>());
            Assert.That(ev.Tracks[0].Instruments[2], Is.TypeOf<ScattererInstrument>());
            Assert.That(ev.Tracks[0].Instruments[3], Is.TypeOf<SilenceInstrument>());
            Assert.That(ev.Tracks[0].Instruments[4], Is.TypeOf<EventInstrument>());
            Assert.That(ev.Tracks[0].Instruments[5], Is.TypeOf<ProgrammerInstrument>());
            Assert.That(ev.Tracks[0].Instruments[6], Is.TypeOf<CommandInstrument>());
            Assert.That(ev.Tracks[0].Instruments[7], Is.TypeOf<SnapshotInstrument>());
            Assert.That(ev.Modulators[0], Is.TypeOf<RandomModulator>());
            Assert.That(ev.Modulators[1], Is.TypeOf<AhdsrModulator>());
            Assert.That(ev.Tracks[0].Instruments[0].Modulators[0], Is.TypeOf<LfoModulator>());
            Assert.That(ev.Markers[0], Is.TypeOf<DestinationMarker>());
            Assert.That(ev.Markers[1], Is.TypeOf<LoopRegion>());
            Assert.That(ev.Markers[2], Is.TypeOf<TransitionMarker>());
            Assert.That(ev.Markers[3], Is.TypeOf<TransitionRegion>());
            Assert.That(ev.Markers[4], Is.TypeOf<SustainPoint>());

            var clip = (ClipInstrument)ev.Tracks[0].Instruments[0];
            Assert.That(clip.Asset, Is.EqualTo(new AssetRef("res://a.ogg")));
            Assert.That(clip.Placement.OnTimeline, Is.True);
            Assert.That(clip.CutMode, Is.EqualTo(CutMode.FadeOut));
            Assert.That(((MultiInstrument)ev.Tracks[0].Instruments[1]).Placement.Parameter, Is.EqualTo(copy.GlobalParameters[0].Id));
            Assert.That(((LfoModulator)clip.Modulators[0]).Shape, Is.EqualTo(LfoShape.Triangle));
            Assert.That(((TransitionMarker)ev.Markers[2]).Destination, Is.EqualTo(ev.Markers[0].Id));
            Assert.That(ev.Automation[0].Curve.Points[0], Is.EqualTo(new CurvePoint(0f, -20f, CurveShape.Smooth)));
            Assert.That(ev.Automation[0].Target, Is.EqualTo(PropertyRef.EventVolume(ev.Id)));
            Assert.That(ev.Macros.Stealing, Is.EqualTo(StealingMode.Quietest));
            Assert.That(ev.Macros.Rolloff, Is.EqualTo(Rolloff.Logarithmic));
            Assert.That(ev.Macros.Doppler, Is.True);
            Assert.That(ev.Macros.DopplerScale, Is.EqualTo(0.5f));
            Assert.That(copy.Mixer.Buses[1].FaderIndex, Is.EqualTo(0));
            Assert.That(copy.Mixer.Buses[0].FaderIndex, Is.EqualTo(int.MaxValue), "default fader position survives the round trip");
            Assert.That(ev.Tempo[1].BeatsPerBar, Is.EqualTo(3));
            Assert.That(copy.Snapshots[0].Values[1].Target, Is.EqualTo(PropertyRef.Effect(copy.Mixer.Buses[1].Id, 0, "threshold")));
            Assert.That(copy.Snapshots[0].Intensity.Sustain, Is.EqualTo(0.8f));
            Assert.That(copy.Mixer.Buses[1].Effects[0].StringParameters["sidechain"], Is.EqualTo("SFX"));
            Assert.That(copy.Mixer.Buses[1].Effects[0].Parameters["ratio"], Is.EqualTo(4f));
            Assert.That(copy.Mixer.Buses[1].Vcas[0], Is.EqualTo(copy.Mixer.Vcas[0].Id));
            Assert.That(copy.AssetLengths["res://child.ogg"], Is.EqualTo(1.5));
            Assert.That(copy.GlobalParameters[1].Labels, Is.EqualTo(new[] { "Grass", "Stone", "Water" }));
            Assert.That(copy.Banks[0].Events, Has.Count.EqualTo(2));
        }
    }

    [Test]
    public void Serialize_UsesDocumentedEncodings()
    {
        AudioProject project = FullProject();
        string json = ProjectJson.Serialize(project, indented: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(json, Does.Contain("\"$type\":\"Clip\""));
            Assert.That(json, Does.Contain("\"$type\":\"TransitionRegion\""));
            Assert.That(json, Does.Contain("\"$type\":\"Transition\","));
            Assert.That(json, Does.Contain("\"$type\":\"Loop\""));
            Assert.That(json, Does.Contain("\"$type\":\"SustainPoint\""));
            Assert.That(json, Does.Contain("\"$type\":\"Ahdsr\""));
            Assert.That(json, Does.Contain("[0,-20,\"Smooth\"]"));
            Assert.That(json, Does.Contain("\"id\":\"00000001000000000000000000000000\""));
            Assert.That(json, Does.Contain("\"asset\":\"res://a.ogg\""));
            Assert.That(json, Does.Contain("{\"kind\":\"EffectParameter\",\"owner\":\"" + project.Mixer.Buses[1].Id + "\",\"effectIndex\":0,\"name\":\"threshold\"}"));
            Assert.That(json, Does.Contain("{\"kind\":\"BusVolumeDb\",\"owner\":\"" + project.Mixer.Buses[1].Id + "\"}"));
            Assert.That(json, Does.Contain("\"stealing\":\"Quietest\""));
            Assert.That(json, Does.Contain("\"rolloff\":\"Logarithmic\""));
            Assert.That(json, Does.Contain("\"faderIndex\":0"));
            Assert.That(json, Does.Not.Contain("onTimeline"));
            Assert.That(json, Does.Not.Contain("isNone"));
            Assert.That(json, Does.Not.Contain("isEmpty"));
        }
    }

    [Test]
    public void Serialize_KeyOrderIsDeterministic()
    {
        string json = ProjectJson.Serialize(FullProject(), indented: false);
        // Object keys: id, then name/path, then the rest ordinally sorted.
        Assert.That(json, Does.StartWith("{\"name\":\"Full\",\"assetLengths\":{\"res://a.ogg\":4,\"res://child.ogg\":1.5},\"audioTables\":[],\"banks\":["));
        Assert.That(json, Does.Contain("\"id\":\"00000001000000000000000000000000\",\"name\":\"Master\",\"bypassEffects\":false,\"effects\":[],\"faderIndex\":2147483647,\"maxVoices\":0,\"mute\":false,\"parent\":\"00000000000000000000000000000000\",\"solo\":false,\"vcas\":[],\"volumeDb\":0}"));
        // Dictionary keys sorted regardless of insertion order.
        Assert.That(json, Does.Contain("\"parameters\":{\"ratio\":4,\"threshold\":-12}"));
    }

    [Test]
    public void Serialize_IsStableAcrossInsertionOrderOfDictionaries()
    {
        var b1 = ProjectBuilder.Minimal();
        b1.Project.AssetLengths["res://z.ogg"] = 1;
        b1.Project.AssetLengths["res://a.ogg"] = 2;
        var b2 = ProjectBuilder.Minimal();
        b2.Project.AssetLengths["res://a.ogg"] = 2;
        b2.Project.AssetLengths["res://z.ogg"] = 1;
        Assert.That(ProjectJson.Serialize(b1.Build()), Is.EqualTo(ProjectJson.Serialize(b2.Build())));
    }

    [Test]
    public void Deserialize_UnknownInstrumentType_Throws()
    {
        string json = ProjectJson.Serialize(ProjectBuilder.Minimal().Build(), indented: false)
            .Replace("\"$type\":\"Clip\"", "\"$type\":\"Granular\"");
        AssertRejects(json);
    }

    [Test]
    public void Deserialize_UnknownMarkerAndModulatorType_Throws()
    {
        var b = ProjectBuilder.Minimal();
        AudioEvent ev = b.Project.Events[0];
        b.AddMarker(ev, new SustainPoint(), 1f);
        b.AddModulator(ev, new RandomModulator(), PropertyRef.EventVolume(ev.Id), 1f);
        string json = ProjectJson.Serialize(b.Build(), indented: false);
        AssertRejects(json.Replace("\"$type\":\"SustainPoint\"", "\"$type\":\"Marker\""));
        AssertRejects(json.Replace("\"$type\":\"Random\"", "\"$type\":\"Wobble\""));
    }

    [Test]
    public void Deserialize_MissingTypeDiscriminator_Throws()
    {
        string json = ProjectJson.Serialize(ProjectBuilder.Minimal().Build(), indented: false)
            .Replace("\"$type\":\"Clip\",", "");
        System.Action act = () => ProjectJson.Deserialize(json);
        Assert.That(Assert.Catch(act), Is.InstanceOf<JsonException>().Or.InstanceOf<System.NotSupportedException>());
    }

    [Test]
    public void Deserialize_BadDirgeId_Throws()
    {
        string json = ProjectJson.Serialize(ProjectBuilder.Minimal().Build(), indented: false)
            .Replace("00000001000000000000000000000000", "not-a-guid");
        AssertRejects(json);
    }

    [Test]
    public void Deserialize_MissingOptionalMembers_KeepsConstructorDefaults()
    {
        AudioProject copy = ProjectJson.Deserialize("{\"name\":\"Sparse\",\"events\":[{\"path\":\"X\",\"tracks\":[{\"instruments\":[{\"$type\":\"Clip\",\"asset\":\"res://x.ogg\"}]}]}]}");
        Instrument instrument = copy.Events[0].Tracks[0].Instruments[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copy.Name, Is.EqualTo("Sparse"));
            Assert.That(copy.Mixer.Buses, Is.Empty);
            Assert.That(instrument.Pitch, Is.EqualTo(1f));
            Assert.That(instrument.Probability, Is.EqualTo(1f));
            Assert.That(instrument.Id.IsNone, Is.False);
            Assert.That(copy.Events[0].Macros.Priority, Is.EqualTo(128));
        }
    }

    [Test]
    public void Deserialize_Null_Throws()
    {
        AssertRejects("null");
    }
}
