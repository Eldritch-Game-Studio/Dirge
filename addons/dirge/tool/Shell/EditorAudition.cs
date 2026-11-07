#nullable enable
#if TOOLS
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Editor playback: owns an <see cref="AudioDriver"/>, an <see cref="AssetCatalog"/> and the auditioned <see cref="DirgeSystem"/>.
/// Players live in a private <see cref="SubViewport"/> that is a 3D audio listener, so spatial events are audible in the editor
/// with the preview listener/emitter. The editor's own bus layout is captured before the first <see cref="AudioDriver.LoadMixer"/>
/// and restored on <see cref="Dispose"/>. While Dirge owns the layout the editor's Audio Buses dock stops processing: its
/// per-frame <c>AudioServer.is_edited()</c> poll would otherwise persist Dirge's audition layout (every <c>add_bus</c>/
/// <c>set_bus_volume_db</c> marks the server edited) to <c>res://default_bus_layout.tres</c> and the project settings.
/// </summary>
public sealed class EditorAudition : AuditionBase
{
    private readonly Node _host;
    private readonly AssetCatalog _assets = new();
    private readonly SubViewport _world;
    private readonly Node3D _players;
    private readonly AudioListener3D _listener;
    private readonly AudioDriver _driver;
    private AudioBusLayout? _editorLayout;
    private readonly Dictionary<DirgeId, int> _busIndexById = new();
    private bool _busIndexValid;
    private readonly Node? _busesDock;

    public EditorAudition(Node host, ProjectDocument document) : base(document)
    {
        _host = host;
        _world = new SubViewport
        {
            Name = "DirgeAuditionWorld",
            Size = new Vector2I(2, 2),
            OwnWorld3D = true,
            AudioListenerEnable3D = true,
            AudioListenerEnable2D = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Disable3D = false,
        };
        var camera = new Camera3D { Name = "Camera", Current = true };
        _listener = new AudioListener3D { Name = "Listener" };
        _players = new Node3D { Name = "Players" };
        _world.AddChild(camera);
        _world.AddChild(_listener);
        _world.AddChild(_players);
        _host.AddChild(_world);
        _listener.MakeCurrent();
        _driver = new AudioDriver(_players, _assets);
        _driver.VoiceFinished += OnVoiceFinished;
        _busesDock = FindEditorAudioBuses(EditorInterface.Singleton.GetBaseControl());
        _busesDock?.SetProcess(false);
        ScheduleRebuild();
    }

    public AudioDriver EngineDriver => _driver;
    public AssetCatalog Assets => _assets;
    protected override AudioDriver? Driver => _driver;

    /// <summary>Engine bus count the editor had before Dirge replaced the layout; 0 when not captured yet.</summary>
    public int CapturedEditorBusCount { get; private set; }

    protected override DirgeSystem CreateSystem()
    {
        _driver.StopAll();
        if (_editorLayout is null)
        {
            _editorLayout = AudioServer.GenerateBusLayout();
            CapturedEditorBusCount = AudioServer.GetBusCount();
        }

        _driver.LoadMixer(Document.Project.Mixer);
        _busIndexValid = false;
        return new DirgeSystem(Document.Project, 0, _assets);
    }

    protected override void Step(double dt)
    {
        DirgeSystem system = System!;
        system.Update(dt);
        _driver.Apply(system.Commands);
        system.Commands.Clear();
    }

    protected override void OnListenerChanged(Attributes3D listener)
    {
        if (_listener.IsInsideTree())
        {
            _listener.GlobalTransform = ShellMath.ToTransform(listener);
        }
    }

    public override (float Left, float Right) BusPeakDb(DirgeId bus)
    {
        if (System is null)
        {
            return (-80f, -80f);
        }

        if (!_busIndexValid)
        {
            _busIndexById.Clear();
            foreach (Bus authored in System.Project.Mixer.Buses)
            {
                int index = _driver.BusIndex(authored.Name);
                if (index >= 0)
                {
                    _busIndexById[authored.Id] = index;
                }
            }

            _busIndexValid = true;
        }

        if (!_busIndexById.TryGetValue(bus, out int busIndex) || busIndex >= AudioServer.GetBusCount())
        {
            return (-80f, -80f);
        }

        float left = AudioServer.GetBusPeakVolumeLeftDb(busIndex, 0);
        float right = AudioServer.GetBusPeakVolumeRightDb(busIndex, 0);
        return (Mathf.Max(left, -80f), Mathf.Max(right, -80f));
    }

    private void OnVoiceFinished(VoiceId voice) => System?.Feedback(new VoiceFinished(voice));

    /// <summary>Stops everything and restores the editor's bus layout.</summary>
    public override void Dispose()
    {
        if (System is not null)
        {
            System.StopAll(StopMode.Immediate);
            System.Update(0);
            _driver.Apply(System.Commands);
            System.Commands.Clear();
        }

        base.Dispose();
        _driver.VoiceFinished -= OnVoiceFinished;
        _driver.StopAll();
        if (_editorLayout is not null)
        {
            // set_bus_layout clears the server's edited flag, so the dock resumes with nothing pending to save. It does not
            // emit bus_layout_changed, so the dock would keep strips for buses that no longer exist (and read their channels
            // every frame); announce the change ourselves.
            AudioServer.SetBusLayout(_editorLayout);
            AudioServer.Singleton.EmitSignal(AudioServer.SignalName.BusLayoutChanged);
            _editorLayout = null;
        }

        if (_busesDock is not null && GodotObject.IsInstanceValid(_busesDock))
        {
            _busesDock.SetProcess(true);
        }

        if (GodotObject.IsInstanceValid(_world))
        {
            _world.GetParent()?.RemoveChild(_world);
            _world.QueueFree();
        }
    }

    /// <summary>The editor's <c>EditorAudioBuses</c> bottom dock (engine class, not exposed to scripts); null when absent.</summary>
    private static Node? FindEditorAudioBuses(Node root)
    {
        if (root.GetClass() == "EditorAudioBuses")
        {
            return root;
        }

        foreach (Node child in root.GetChildren())
        {
            if (FindEditorAudioBuses(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
#endif
