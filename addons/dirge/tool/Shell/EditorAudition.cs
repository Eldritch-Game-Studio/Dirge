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
/// and restored on <see cref="Dispose"/>.
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
            AudioServer.SetBusLayout(_editorLayout);
            _editorLayout = null;
        }

        if (GodotObject.IsInstanceValid(_world))
        {
            _world.GetParent()?.RemoveChild(_world);
            _world.QueueFree();
        }
    }
}
#endif
