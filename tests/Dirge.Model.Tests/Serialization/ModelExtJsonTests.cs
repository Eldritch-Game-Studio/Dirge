using System.Collections.Generic;
using Dirge.Project;
using Dirge.Serialization;
using NUnit.Framework;

namespace Dirge.Tests.Serialization;

[TestFixture]
public sealed class ModelExtJsonTests
{
    internal static AudioProject ExtendedProject()
    {
        var b = new ProjectBuilder("Ext");
        Bus music = b.AddBus("Music", volumeDb: -3f);
        ParameterDefinition intensity = b.AddGlobalParameter("Intensity");

        AudioEvent ev = b.AddEvent("Music/Main", music);
        ev.IsConductor = true;
        ev.Macros.StartQuantization = Quantization.Bar;
        ev.Macros.ScheduleLookahead = 0.25f;
        ev.Tags.Add("music");
        ev.Tags.Add("Adaptive");
        Track track = b.AddTrack(ev);
        ClipInstrument layer = b.AddClip(track, "res://layer.ogg");
        layer.SyncGroup = 2;
        DestinationMarker dest = b.AddMarker(ev, new DestinationMarker(), 8f, "Dest");
        TransitionMarker transition = b.AddMarker(ev, new TransitionMarker { Destination = dest.Id, TransitionLength = 1f }, 4f, "Go");
        transition.TransitionInstruments.Add(new ClipInstrument { Id = b.NextId(), Name = "Riser", Asset = new AssetRef("res://riser.ogg"), Placement = new Placement { Start = 0f, Length = 1f } });
        TransitionRegion region = b.AddMarker(ev, new TransitionRegion { Destination = dest.Id, Length = 2f, TransitionLength = 0.5f }, 5f, "Region");
        region.TransitionInstruments.Add(new SilenceInstrument { Id = b.NextId(), Name = "Gap", Placement = new Placement { Start = 0f, Length = 0.5f } });
        b.AddMarker(ev, new MagnetRegion { Length = 2f }, 8f, "Snap");

        var table = new AudioTable { Id = b.NextId(), Name = "Dialogue" };
        table.Entries["greeting"] = new Dictionary<string, AssetRef> { ["fr"] = new AssetRef("res://fr/g.ogg"), ["en"] = new AssetRef("res://en/g.ogg") };
        table.Entries["farewell"] = new Dictionary<string, AssetRef> { ["en"] = new AssetRef("res://en/f.ogg") };
        b.Project.AudioTables.Add(table);

        var po = new PlatformOverride { Platform = "Android" };
        po.Values.Add(new SnapshotValue { Target = PropertyRef.BusVolume(music.Id), Value = -6f });
        b.Project.PlatformOverrides.Add(po);

        var lane = new AutomationLane { Id = b.NextId(), Target = PropertyRef.BusVolume(music.Id), Parameter = intensity.Id };
        lane.Curve.Points.Add(new CurvePoint(0f, 0f));
        lane.Curve.Points.Add(new CurvePoint(1f, -12f, CurveShape.Smooth));
        b.Project.MixerAutomation.Add(lane);
        return b.Build();
    }

    [Test]
    public void RoundTrip_ExtendedProject_ReserializesIdentically()
    {
        AudioProject original = ExtendedProject();
        string json = ProjectJson.Serialize(original);
        AudioProject copy = ProjectJson.Deserialize(json);
        Assert.That(ProjectJson.Serialize(copy), Is.EqualTo(json));
    }

    [Test]
    public void RoundTrip_PreservesNewTypesAndValues()
    {
        AudioProject copy = ProjectJson.Deserialize(ProjectJson.Serialize(ExtendedProject()));
        AudioEvent ev = copy.Events[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ev.IsConductor, Is.True);
            Assert.That(ev.Macros.StartQuantization, Is.EqualTo(Quantization.Bar));
            Assert.That(ev.Tracks[0].Instruments[0].SyncGroup, Is.EqualTo(2));
            var transition = (TransitionMarker)ev.Markers[1];
            Assert.That(transition.TransitionLength, Is.EqualTo(1f));
            Assert.That(transition.TransitionInstruments, Has.Count.EqualTo(1));
            Assert.That(transition.TransitionInstruments[0], Is.TypeOf<ClipInstrument>());
            Assert.That(((ClipInstrument)transition.TransitionInstruments[0]).Asset.Path, Is.EqualTo("res://riser.ogg"));
            var region = (TransitionRegion)ev.Markers[2];
            Assert.That(region.TransitionLength, Is.EqualTo(0.5f));
            Assert.That(region.TransitionInstruments[0], Is.TypeOf<SilenceInstrument>());
            Assert.That(ev.Markers[3], Is.TypeOf<MagnetRegion>());
            Assert.That(((MagnetRegion)ev.Markers[3]).Length, Is.EqualTo(2f));

            Assert.That(copy.AudioTables, Has.Count.EqualTo(1));
            Assert.That(copy.AudioTables[0].Name, Is.EqualTo("Dialogue"));
            Assert.That(copy.AudioTables[0].Entries["greeting"]["fr"].Path, Is.EqualTo("res://fr/g.ogg"));
            Assert.That(copy.AudioTables[0].Entries["farewell"], Has.Count.EqualTo(1));
            Assert.That(copy.PlatformOverrides[0].Platform, Is.EqualTo("Android"));
            Assert.That(copy.PlatformOverrides[0].Values[0].Value, Is.EqualTo(-6f));
            Assert.That(copy.MixerAutomation, Has.Count.EqualTo(1));
            Assert.That(copy.MixerAutomation[0].Curve.Points[1].ShapeToNext, Is.EqualTo(CurveShape.Smooth));
        }
    }

    [Test]
    public void Json_UsesMagnetDiscriminator_AndSortsTableLocales()
    {
        string json = ProjectJson.Serialize(ExtendedProject());
        Assert.That(json, Does.Contain("\"$type\": \"Magnet\""));
        Assert.That(json.IndexOf("\"en\": \"res://en/g.ogg\"", System.StringComparison.Ordinal),
            Is.LessThan(json.IndexOf("\"fr\": \"res://fr/g.ogg\"", System.StringComparison.Ordinal)), "locales are written in ordinal order");
    }

    [Test]
    public void Deserialize_OldDocumentWithoutNewLists_Works()
    {
        string json = ProjectJson.Serialize(ProjectBuilder.Minimal().Build())
            .Replace("\"audioTables\": [],", "").Replace("\"platformOverrides\": [],", "").Replace("\"mixerAutomation\": [],", "");
        Assert.That(json, Does.Not.Contain("audioTables"));
        AudioProject copy = ProjectJson.Deserialize(json);
        Assert.That(copy.AudioTables, Is.Empty);
        Assert.That(copy.Events[0].Tracks[0].Instruments[0].SyncGroup, Is.EqualTo(0));
    }

    [Test]
    public void RoundTrip_PreservesEventTags_InOrder()
    {
        string json = ProjectJson.Serialize(ExtendedProject());
        Assert.That(json, Does.Contain("\"tags\": ["), "tags are written as a plain string array");
        Assert.That(json.IndexOf("\"music\"", System.StringComparison.Ordinal), Is.LessThan(json.IndexOf("\"Adaptive\"", System.StringComparison.Ordinal)), "authored order is preserved, not sorted");
        AudioProject copy = ProjectJson.Deserialize(json);
        Assert.That(copy.Events[0].Tags, Is.EqualTo(new[] { "music", "Adaptive" }));
    }

    [Test]
    public void Deserialize_OldDocumentWithoutTags_LeavesTagsEmpty()
    {
        string json = ProjectJson.Serialize(ProjectBuilder.Minimal().Build()).Replace("\"tags\": [],", "");
        Assert.That(json, Does.Not.Contain("\"tags\""));
        Assert.That(ProjectJson.Deserialize(json).Events[0].Tags, Is.Empty);
    }
}
