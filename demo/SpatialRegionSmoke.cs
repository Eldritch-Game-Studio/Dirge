using System;
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Runtime;
using Godot;

namespace DirgeDemo;

/// <summary>Real-engine lifecycle assertions using the demo's existing driver/mixer, not a mock driver or speaker proof.</summary>
internal sealed class SpatialRegionSmoke
{
    private readonly AudioDriver _driver;
    private readonly Node _parent;
    private readonly HashSet<VoiceId> _finished = new();
    private readonly List<VoiceId> _voices = new();
    private readonly Region _a;
    private readonly Region _b;
    private readonly VoiceId _voiceA;
    private readonly VoiceId _voiceB;
    private readonly VoiceId _oneShot;
    private int _next = 1000;

    private sealed record Region(SpatialRegionId Id, SubViewport Viewport, Node3D Host, AudioListener3D Listener);

    public SpatialRegionSmoke(AudioDriver driver, Node parent)
    {
        _driver = driver;
        _parent = parent;
        _driver.VoiceFinished += Finished;
        _a = CreateRegion(101);
        _b = CreateRegion(102);
        _voiceA = Play(_a.Id);
        _voiceB = Play(_b.Id);
        _oneShot = Play(_a.Id, loop: false);
        Require(Player(_voiceA).GetWorld3D() == _a.Host.GetWorld3D(), "A player not in A world");
        Require(Player(_voiceB).GetWorld3D() == _b.Host.GetWorld3D(), "B player not in B world");
        Require(Player(_voiceA).GetWorld3D() != Player(_voiceB).GetWorld3D(), "same-coordinate voices share a world");
    }

    private Region CreateRegion(int id)
    {
        var viewport = new SubViewport { OwnWorld3D = true, AudioListenerEnable3D = true, Size = new Vector2I(16, 16) };
        _parent.AddChild(viewport);
        var host = new Node3D();
        viewport.AddChild(host);
        var camera = new Camera3D();
        host.AddChild(camera);
        camera.MakeCurrent();
        var listener = new AudioListener3D();
        host.AddChild(listener);
        listener.MakeCurrent();
        var region = new Region(new SpatialRegionId(id), viewport, host, listener);
        _driver.RegisterSpatialRegion(region.Id, host, listener);
        return region;
    }

    private static Attributes3D Attributes(SpatialRegionId region) => Attributes3D.Origin with
    {
        Position = new System.Numerics.Vector3(0, 0, -4), Region = region,
    };

    private VoicePlay Command(VoiceId voice, SpatialRegionId region, bool loop = true, int sync = 0, bool spatial = true) =>
        new(voice, new AssetRef(loop ? DemoProject.WindAsset : DemoProject.ClickAsset), "Master", 0, loop, -30, 1,
            spatial, Attributes(region), 1, 20, sync);

    private VoiceId Next()
    {
        var voice = new VoiceId(_next++);
        _voices.Add(voice);
        return voice;
    }

    private VoiceId Play(SpatialRegionId region, bool loop = true)
    {
        VoiceId voice = Next();
        _driver.Apply(Command(voice, region, loop));
        return voice;
    }

    private AudioStreamPlayer3D Player(VoiceId voice) =>
        _driver.PlayerOf(voice) as AudioStreamPlayer3D ?? throw new InvalidOperationException($"Missing spatial player {voice}");

    private void Finished(VoiceId voice)
    {
        if (voice.Value >= 1000) _finished.Add(voice);
    }

    public void Finish()
    {
        try
        {
            Require(_finished.Contains(_oneShot) && _driver.PlayerOf(_oneShot) is null, "natural spatial finish did not release the voice");
            AudioStreamPlayer3D old = Player(_voiceA);
            Require(_driver.UnregisterSpatialRegion(_a.Id), "A unregister failed");
            Require(_driver.PlayerOf(_voiceA) is null && _finished.Contains(_voiceA), "withdrawal did not finish A");
            Require(old.GetParent() == _driver.Host && old.Stream is null && old.Transform == Transform3D.Identity, "withdrawn slot retained region state");
            Require(Player(_voiceB).GetWorld3D() == _b.Host.GetWorld3D(), "A withdrawal disturbed B");
            VoiceId reused = Play(_b.Id);
            Require(Player(reused) == old && old.GetWorld3D() == _b.Host.GetWorld3D(), "pooled A slot did not become a clean B slot");
            VoiceId unknown = Play(_a.Id);
            Require(_driver.PlayerOf(unknown) is null && _finished.Contains(unknown), "unknown region fell back to default");
            _driver.Apply(new VoiceSet3D(reused, Attributes(_a.Id)));
            Require(_driver.PlayerOf(reused) is null && _finished.Contains(reused), "invalid migration did not fail closed");
            _driver.RegisterSpatialRegion(_a.Id, _a.Host, _a.Listener);
            VoiceId migrating = Play(_a.Id);
            _driver.Apply(new VoiceSetPaused(migrating, true));
            _driver.Apply(new VoiceSet3D(migrating, Attributes(_b.Id)));
            Require(Player(migrating).GetWorld3D() == _b.Host.GetWorld3D() && Player(migrating).StreamPaused, "migration lost world/pause state");
            _driver.Apply(new VoiceSet3D(migrating, Attributes(default)));
            Require(Player(migrating).GetParent() == _driver.Host, "legacy route not restored");
            _driver.Apply(new VoiceStop(migrating));
            Require(_driver.PlayerOf(migrating) is null, "stop did not release migrated voice");
            const string missing = "res://spatial-smoke-unavailable.wav";
            _driver.Assets.Register(missing, null); // A real cached load failure, with no ResourceLoader log spam.
            VoiceId failed = Next();
            _driver.Apply(Command(failed, _a.Id) with { Asset = new AssetRef(missing), Loop = false });
            Require(_driver.PlayerOf(failed) is null && _finished.Contains(failed), "asset failure retained region ownership");
            Require(old.GetParent() == _driver.Host && old.Stream is null, "failed slot retained native world/stream");
            _driver.Assets.Evict(missing);

            var batch = new CommandBuffer();
            VoiceId groupA = Next(), groupB = Next();
            batch.Add(Command(groupA, _a.Id, sync: 41));
            batch.Add(Command(groupB, _b.Id, sync: 41));
            _driver.Apply(batch);
            Require(_driver.PlayerOf(groupA) != _driver.PlayerOf(groupB), "sync merged distinct worlds");
            _driver.Apply(new VoiceStop(groupA));
            _driver.Apply(new VoiceStop(groupB));
            batch.Clear();
            VoiceId layerA = Next(), layerB = Next();
            batch.Add(Command(layerA, _a.Id, sync: 42));
            batch.Add(Command(layerB, _a.Id, sync: 42));
            _driver.Apply(batch);
            Require(_driver.PlayerOf(layerA) == _driver.PlayerOf(layerB), "same-world sync no longer groups");
            _driver.Apply(new VoiceSet3D(layerA, Attributes(_b.Id)));
            Require(Player(layerA).GetWorld3D() == _b.Host.GetWorld3D(), "migrating sync layer not moved");
            Require(Player(layerB).GetWorld3D() == _a.Host.GetWorld3D(), "sync sibling dragged into unrelated world");

            // A listener can exit while its host/viewport is traversing children. Such native slots must be retired,
            // not illegally reparented and later reused after their Godot objects have been freed.
            _a.Host.RemoveChild(_a.Listener);
            Require(_driver.PlayerOf(layerB) is null && _finished.Contains(layerB), "listener exit retained a live voice");
            _a.Host.AddChild(_a.Listener);
            _a.Listener.MakeCurrent();
            _driver.RegisterSpatialRegion(_a.Id, _a.Host, _a.Listener);
            VoiceId afterExit = Play(_a.Id);
            Require(Player(afterExit).GetWorld3D() == _a.Host.GetWorld3D(), "reuse after listener exit has wrong world");
            _parent.RemoveChild(_a.Viewport);
            Require(_driver.PlayerOf(afterExit) is null && _finished.Contains(afterExit), "world subtree exit retained a voice");
            _a.Viewport.QueueFree();

            _b.Viewport.World3D = new World3D();
            _driver.Apply(new CommandBuffer());
            Require(_driver.PlayerOf(_voiceB) is null && _driver.PlayerOf(layerA) is null, "world replacement retained old-world voices");
            VoiceId legacy = Play(default);
            Require(Player(legacy).GetParent() == _driver.Host, "default route changed");
            VoiceId shared = Next();
            _driver.Apply(Command(shared, new SpatialRegionId(-99), spatial: false));
            Require(_driver.PlayerOf(shared) is AudioStreamPlayer && _driver.PlayerOf(shared)!.GetParent() == _driver.Host,
                "nonspatial shared audio depends on region registration");
            GD.Print("DIRGE_SPATIAL_REGION_SMOKE PASS (native world/lifecycle assertions; not audible-output proof)");
        }
        finally
        {
            foreach (VoiceId voice in _voices) _driver.Apply(new VoiceStop(voice));
            _driver.UnregisterSpatialRegion(_a.Id);
            _driver.UnregisterSpatialRegion(_b.Id);
            _driver.VoiceFinished -= Finished;
            if (GodotObject.IsInstanceValid(_a.Viewport)) _a.Viewport.QueueFree();
            _b.Viewport.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Spatial region smoke: " + message);
    }
}
