using System.Collections.Generic;
using System.Numerics;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Serialization;
using Dirge.Tool.Browser;
using Dirge.Tool.Transport;
using NUnit.Framework;

namespace Dirge.Tests.Tool;

/// <summary>Pure logic of the Browser and Transport panes (linked into this project; the panes themselves need Godot).</summary>
[TestFixture]
public sealed class BrowserTransportLogicTests
{
    [Test]
    public void EventTree_GroupsByFolder_FoldersFirst_Sorted()
    {
        var b = new ProjectBuilder();
        b.AddEvent("UI/Click");
        b.AddEvent("Ambience/Wind");
        b.AddEvent("Ambience/Birds/Morning");
        b.AddEvent("Zeta");
        b.AddEvent("Alpha");

        EventTreeNode root = EventTree.Build(b.Project.Events);

        Assert.That(Names(root.Children), Is.EqualTo(new[] { "Ambience", "UI", "Alpha", "Zeta" }));
        EventTreeNode ambience = root.Children[0];
        Assert.That(ambience.IsFolder, Is.True);
        Assert.That(ambience.FullPath, Is.EqualTo("Ambience"));
        Assert.That(Names(ambience.Children), Is.EqualTo(new[] { "Birds", "Wind" }));
        Assert.That(ambience.Children[0].Children[0].FullPath, Is.EqualTo("Ambience/Birds/Morning"));
        Assert.That(ambience.Children[1].EventId, Is.EqualTo(b.Project.Events[1].Id));
    }

    [Test]
    public void EventTree_ExtraFolders_AndFilter()
    {
        var b = new ProjectBuilder();
        b.AddEvent("UI/Click");
        b.AddEvent("Ambience/Wind");

        EventTreeNode root = EventTree.Build(b.Project.Events, new[] { "Music/Combat" });
        Assert.That(Names(root.Children), Is.EqualTo(new[] { "Ambience", "Music", "UI" }));
        Assert.That(root.Children[1].Children[0].FullPath, Is.EqualTo("Music/Combat"));
        Assert.That(root.Children[1].Children[0].IsFolder, Is.True);

        EventTreeNode filtered = EventTree.Build(b.Project.Events, new[] { "Music/Combat" }, "win");
        Assert.That(Names(filtered.Children), Is.EqualTo(new[] { "Ambience" }));
        Assert.That(filtered.Children[0].Children[0].Name, Is.EqualTo("Wind"));
    }

    [Test]
    public void EventTree_TagFilter_KeepsOnlyTaggedEvents_AndDropsExtraFolders()
    {
        var b = new ProjectBuilder();
        b.AddEvent("UI/Click").Tags.Add("ui");
        AudioEvent wind = b.AddEvent("Ambience/Wind");
        wind.Tags.Add("ambient");
        wind.Tags.Add("loop");
        b.AddEvent("Ambience/Birds").Tags.Add("ambient");
        b.AddEvent("Music/Main");

        EventTreeNode ambient = EventTree.Build(b.Project.Events, new[] { "Empty" }, null, "ambient");
        Assert.That(Names(ambient.Children), Is.EqualTo(new[] { "Ambience" }));
        Assert.That(Names(ambient.Children[0].Children), Is.EqualTo(new[] { "Birds", "Wind" }));

        EventTreeNode both = EventTree.Build(b.Project.Events, null, "wi", "ambient");
        Assert.That(both.Children[0].Children.Count, Is.EqualTo(1), "search and tag filters intersect");
        Assert.That(both.Children[0].Children[0].Name, Is.EqualTo("Wind"));

        Assert.That(EventTree.Build(b.Project.Events, null, null, "Ambient").Children, Is.Empty, "tags are case-sensitive");
        Assert.That(EventTree.Build(b.Project.Events, new[] { "Empty" }, null, "").Children.Count, Is.EqualTo(4), "empty tag = no filter");
        Assert.That(EventTree.AllTags(b.Project.Events), Is.EqualTo(new[] { "ambient", "loop", "ui" }));
    }

    [Test]
    public void EventTree_NormalizeTag()
    {
        Assert.That(EventTree.NormalizeTag("  ambient "), Is.EqualTo("ambient"));
        Assert.That(EventTree.NormalizeTag("two   words"), Is.EqualTo("two words"));
        Assert.That(EventTree.NormalizeTag("   "), Is.Null);
        Assert.That(EventTree.NormalizeTag("a,b"), Is.Null);
    }

    [Test]
    public void EventTree_PathHelpers()
    {
        Assert.That(EventTree.FolderOf("A/B/C"), Is.EqualTo("A/B"));
        Assert.That(EventTree.FolderOf("C"), Is.EqualTo(""));
        Assert.That(EventTree.NameOf("A/B/C"), Is.EqualTo("C"));
        Assert.That(EventTree.Join("", "X"), Is.EqualTo("X"));
        Assert.That(EventTree.Join("A", "X"), Is.EqualTo("A/X"));
        Assert.That(EventTree.IsUnder("A/B/C", "A/B"), Is.True);
        Assert.That(EventTree.IsUnder("A/BC", "A/B"), Is.False);
        Assert.That(EventTree.IsUnder("A", ""), Is.True);
        Assert.That(EventTree.Reparent("A/B/C", "A/B", "X"), Is.EqualTo("X/C"));
        Assert.That(EventTree.Reparent("C", "", "X/Y"), Is.EqualTo("X/Y/C"));
        Assert.That(EventTree.Reparent("A/C", "A", ""), Is.EqualTo("C"));
        Assert.That(EventTree.IsValidName("Wind"), Is.True);
        Assert.That(EventTree.IsValidName("A/B"), Is.False);
        Assert.That(EventTree.IsValidName("  "), Is.False);

        var b = new ProjectBuilder();
        b.AddEvent("UI/Click");
        b.AddEvent("UI/Click 2");
        Assert.That(EventTree.UniquePath(b.Project.Events, "UI/Click"), Is.EqualTo("UI/Click 3"));
        Assert.That(EventTree.UniquePath(b.Project.Events, "UI/Hover"), Is.EqualTo("UI/Hover"));
    }

    [Test]
    public void EventCloner_FreshIds_InternalReferencesFollow_ExternalStay()
    {
        var b = new ProjectBuilder();
        Bus bus = b.AddBus("Ambience");
        ParameterDefinition global = b.AddGlobalParameter("Wind");
        AudioEvent other = b.AddEvent("Other");
        AudioEvent source = b.AddEvent("Music/Calm", bus);
        ParameterDefinition local = b.AddLocalParameter(source, "Intensity");
        var track = new Track { Id = b.NextId(), Name = "Layer" };
        var clip = new ClipInstrument { Id = b.NextId(), Name = "Clip", Asset = new AssetRef("res://a.wav") };
        clip.Placement.Parameter = local.Id;
        clip.Conditions.Add(new TriggerCondition { Parameter = global.Id, Minimum = 0f, Maximum = 1f });
        clip.Modulators.Add(new RandomModulator { Id = b.NextId(), Target = PropertyRef.InstrumentVolume(clip.Id), Amount = 3f });
        track.Instruments.Add(clip);
        track.Instruments.Add(new EventInstrument { Id = b.NextId(), Name = "Nested", Event = other.Id });
        source.Tracks.Add(track);
        source.Automation.Add(new AutomationLane { Id = b.NextId(), Target = PropertyRef.EventVolume(source.Id), Parameter = local.Id });
        source.Automation.Add(new AutomationLane { Id = b.NextId(), Target = PropertyRef.BusVolume(bus.Id), Parameter = global.Id });
        var destination = new DestinationMarker { Id = b.NextId(), Name = "Verse", Position = 4f };
        source.Markers.Add(destination);
        source.Markers.Add(new TransitionMarker { Id = b.NextId(), Name = "ToVerse", Position = 8f, Destination = destination.Id });
        source.Modulators.Add(new LfoModulator { Id = b.NextId(), Target = PropertyRef.EventPitch(source.Id), Amount = 0.1f });

        string before = ProjectJson.Serialize(b.Project);
        AudioEvent clone = EventCloner.Clone(b.Project, source, "Music/Calm copy");

        // Source project untouched.
        Assert.That(ProjectJson.Serialize(b.Project), Is.EqualTo(before));
        Assert.That(clone.Path, Is.EqualTo("Music/Calm copy"));
        Assert.That(clone.Bus, Is.EqualTo(bus.Id), "external bus reference kept");

        var oldIds = new HashSet<DirgeId> { source.Id, local.Id, track.Id, clip.Id, clip.Modulators[0].Id, track.Instruments[1].Id, source.Automation[0].Id, source.Automation[1].Id, destination.Id, source.Markers[1].Id, source.Modulators[0].Id };
        var newIds = new HashSet<DirgeId>();
        Track cTrack = clone.Tracks[0];
        var cClip = (ClipInstrument)cTrack.Instruments[0];
        var cNested = (EventInstrument)cTrack.Instruments[1];
        var cTransition = (TransitionMarker)clone.Markers[1];
        foreach (DirgeId id in new[] { clone.Id, clone.LocalParameters[0].Id, cTrack.Id, cClip.Id, cClip.Modulators[0].Id, cNested.Id, clone.Automation[0].Id, clone.Automation[1].Id, clone.Markers[0].Id, cTransition.Id, clone.Modulators[0].Id })
        {
            Assert.That(id.IsNone, Is.False);
            Assert.That(oldIds, Does.Not.Contain(id), "every owned object gets a fresh id");
            Assert.That(newIds.Add(id), Is.True, "fresh ids are unique");
        }

        Assert.That(cClip.Placement.Parameter, Is.EqualTo(clone.LocalParameters[0].Id), "sheet placement follows local parameter");
        Assert.That(cClip.Conditions[0].Parameter, Is.EqualTo(global.Id), "global condition untouched");
        Assert.That(cClip.Modulators[0].Target.Owner, Is.EqualTo(cClip.Id), "modulator target follows instrument");
        Assert.That(cNested.Event, Is.EqualTo(other.Id), "nested external event untouched");
        Assert.That(clone.Automation[0].Target.Owner, Is.EqualTo(clone.Id));
        Assert.That(clone.Automation[0].Parameter, Is.EqualTo(clone.LocalParameters[0].Id));
        Assert.That(clone.Automation[1].Target.Owner, Is.EqualTo(bus.Id));
        Assert.That(clone.Automation[1].Parameter, Is.EqualTo(global.Id));
        Assert.That(cTransition.Destination, Is.EqualTo(clone.Markers[0].Id), "transition destination follows marker");
        Assert.That(clone.Modulators[0].Target.Owner, Is.EqualTo(clone.Id));
        Assert.That(cClip.Asset.Path, Is.EqualTo("res://a.wav"));

        // Clone is a valid addition: the project with both events validates and round-trips.
        b.Project.Events.Add(clone);
        Assert.That(Dirge.Validation.ProjectValidator.Validate(b.Project), Is.Empty);
        string text = ProjectJson.Serialize(b.Project);
        foreach (DirgeId id in oldIds) Assert.That(CountOf(text, id.ToString()), Is.EqualTo(CountOf(before, id.ToString())), "old ids are referenced exactly as often as before");
    }

    [Test]
    public void PreviewReadout_MatchesSpatialConventions()
    {
        Attributes3D listener = Attributes3D.Origin;
        var emitter = new Attributes3D(new Vector3(3f, 0f, -4f), new Vector3(1f, 0f, 0f), -Vector3.UnitZ, Vector3.UnitY);

        PreviewReadout r = PreviewReadout.Compute(emitter, listener, 1f, 21f);

        Assert.That(r.Distance, Is.EqualTo(5f).Within(1e-5f));
        Assert.That(r.DistanceNormalized, Is.EqualTo(0.2f).Within(1e-5f));
        Assert.That(r.Direction, Is.EqualTo(36.8699f).Within(1e-3f), "right of forward is positive");
        Assert.That(r.Elevation, Is.EqualTo(0f).Within(1e-5f));
        Assert.That(r.ConeAngle, Is.EqualTo(143.1301f).Within(1e-3f));
        Assert.That(r.Orientation, Is.EqualTo(0f).Within(1e-5f));
        Assert.That(r.Speed, Is.EqualTo(0.6f).Within(1e-5f), "receding along the axis is positive");
        Assert.That(r.SpeedAbsolute, Is.EqualTo(1f).Within(1e-5f));
        Assert.That(r.Get(BuiltInParameter.Distance), Is.EqualTo(r.Distance));

        var behindLeft = new Attributes3D(new Vector3(-1f, 2f, 1f), Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        PreviewReadout b = PreviewReadout.Compute(behindLeft, listener, 1f, 1f);
        Assert.That(b.Direction, Is.EqualTo(-135f).Within(1e-3f));
        Assert.That(b.Elevation, Is.EqualTo(54.7356f).Within(1e-3f));
        Assert.That(b.Orientation, Is.EqualTo(90f).Within(1e-3f));
        Assert.That(b.DistanceNormalized, Is.EqualTo(1f), "degenerate range: at/after max is 1");
    }

    [Test]
    public void PreviewReadout_RotateYaw()
    {
        Vector3 left = PreviewReadout.RotateYaw(-Vector3.UnitZ, 90f);
        Assert.That(left.X, Is.EqualTo(-1f).Within(1e-5f));
        Assert.That(left.Z, Is.EqualTo(0f).Within(1e-5f));
        Vector3 back = PreviewReadout.RotateYaw(-Vector3.UnitZ, 180f);
        Assert.That(back.Z, Is.EqualTo(1f).Within(1e-5f));
        Assert.That(PreviewReadout.SignedYaw(-Vector3.UnitZ, Vector3.UnitY, left), Is.EqualTo(-90f).Within(1e-3f));
    }

    private static string[] Names(List<EventTreeNode> nodes)
    {
        var names = new string[nodes.Count];
        for (int i = 0; i < nodes.Count; i++) names[i] = nodes[i].Name;
        return names;
    }

    private static int CountOf(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, System.StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal)) count++;
        return count;
    }
}
