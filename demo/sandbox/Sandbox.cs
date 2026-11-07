using System;
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Profiling;
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Serialization;
using Godot;

namespace DirgeDemo;

/// <summary>
/// 3D sandbox: the camera is the listener (WASD + mouse look), a bird emitter orbits the player, and the keyboard drives the
/// authored project from <see cref="DemoProject.BuildSandbox"/>. With <c>-- --smoke</c> it runs a 6 s scripted session under a
/// <see cref="Recorder"/>, replays the recording on a fresh model and asserts the command streams match, music ducked during
/// dialogue and the Combat layers landed on a beat; prints <c>SANDBOX_SMOKE PASS</c> and exits 0, or <c>SANDBOX_SMOKE FAIL</c> and exits 1.
/// </summary>
public partial class Sandbox : Node3D
{
    public const string ProjectFile = "res://demo/sandbox/sandbox.dirge.json";
    public const string RecordingFile = "user://sandbox-recording.json";

    private const float MoveSpeed = 4f;
    private const float LookSensitivity = 0.0025f;
    private const float BirdOrbitRadius = 6f;
    private const float BirdOrbitHeight = 2.5f;
    private const float BirdOrbitSpeed = 0.35f;
    private const double SmokeSeconds = 6.0;
    /// <summary>Tolerance for "started on a beat": one 60 fps frame plus slack, as the assignment specifies.</summary>
    private const double BeatPhaseTolerance = 0.02;

    private DirgeServer _server = null!;
    private Camera3D _player = null!;
    private Node3D _birdEmitter = null!;
    private Label _hud = null!;

    private EventInstance? _music;
    private EventInstance? _birds;
    private float _yaw;
    private float _pitch;
    private float _birdAngle;
    private bool _threatHigh;
    private bool _germanLocale;
    private double _hudTimer;

    // ---- screenshot proof (--screenshot=<path>) ----
    private string? _screenshotPath;
    private int _screenshotFrame;
    private bool _screenshotTaken;

    // ---- smoke ----
    private bool _smoke;
    private int _smokeStep;
    private Recorder? _recorder;
    private readonly Dictionary<Type, int> _commandCounts = new();
    private int _combatPlays;
    private int _stingerPlays;
    private int _birdPlays;
    private int _lineEnPlays;
    private int _lineDePlays;
    private double _worstCombatPhase;
    private double _worstStingerPhase;
    private float _musicMinDbDuringDialogue = float.PositiveInfinity;
    private float _musicLastDb;
    private bool _dialogueStarted;

    /// <summary>Visual proof: flip Threat, start a line so ducking is visible on the HUD, then save the viewport and quit.</summary>
    private void ScreenshotStep()
    {
        if (_screenshotTaken)
        {
            return;
        }

        int frame = _screenshotFrame++;
        switch (frame)
        {
            case 30:
                ToggleThreat();
                break;
            case 150:
                _server.StartEvent(DemoProject.DialogueEvent);
                break;
            case 200:
                _screenshotTaken = true;
                UpdateHud();
                CallDeferred(nameof(SaveScreenshot));
                break;
        }
    }

    private async void SaveScreenshot()
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Error error;
        using (Image image = GetViewport().GetTexture().GetImage())
        {
            error = image.SavePng(_screenshotPath!);
            GD.Print($"SANDBOX_SCREENSHOT {_screenshotPath} {error} {image.GetWidth()}x{image.GetHeight()}");
        }

        Quit(error == Error.Ok ? 0 : 1);
    }

    /// <summary>
    /// Stop every event, let the server apply the stops and the mixer run a few blocks, then quit. A player stopped during tree
    /// teardown never gets its mix step, which the engine reports as a leaked playback/stream at exit.
    /// </summary>
    private async void Quit(int code)
    {
        _server.CommandsEmitted -= OnCommands;
        _server.StopAllEvents(immediate: true);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree().CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        GetTree().Quit(code);
    }

    public override void _EnterTree()
    {
        EnsureProjectFile();
    }

    public override void _Ready()
    {
        _server = GetNode<DirgeServer>("DirgeServer");
        _player = GetNode<Camera3D>("Player");
        _birdEmitter = GetNode<Node3D>("BirdEmitter");
        _hud = GetNode<Label>("Hud/Label");
        string[] args = OS.GetCmdlineUserArgs();
        _smoke = Array.IndexOf(args, "--smoke") >= 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--screenshot=", StringComparison.Ordinal))
            {
                _screenshotPath = args[i].Substring("--screenshot=".Length);
            }
        }

        if (!_server.IsLoaded)
        {
            Fail("project failed to load");
            return;
        }

        _server.System.Locale = "en";
        _server.CommandsEmitted += OnCommands;
        _yaw = _player.Rotation.Y;
        _pitch = _player.Rotation.X;

        if (_smoke)
        {
            Engine.MaxFps = 60;
            _recorder = new Recorder(_server.System);
            GD.Print("SANDBOX_SMOKE start");
        }
        else
        {
            GD.Print("Dirge sandbox: 1 Threat 0/1 · 2 stinger · 3 dialogue · L locale en/de · Space click · F9 HUD · click to capture mouse, Esc to release.");
        }

        PushListener();
        StartAmbientLoops();
        UpdateHud();
    }

    private void StartAmbientLoops()
    {
        _music = _server.System.GetEvent(DemoProject.MusicEvent).CreateInstance();
        _music.Start();
        _birds = _server.System.GetEvent(DemoProject.BirdsEvent).CreateInstance();
        _birds.Set3DAttributes(ToAttributes(_birdEmitter.GlobalTransform, System.Numerics.Vector3.Zero));
        _birds.Start();
    }

    public override void _Process(double delta)
    {
        if (!_server.IsLoaded)
        {
            return;
        }

        if (!_smoke)
        {
            Move((float)delta);
        }

        OrbitBirds((float)delta);
        PushListener();

        if (_smoke)
        {
            SmokeStep();
        }
        else if (_screenshotPath is not null)
        {
            ScreenshotStep();
        }

        _hudTimer += delta;
        if (_hudTimer >= 0.1)
        {
            _hudTimer = 0;
            UpdateHud();
        }
    }

    // ---- interaction -----------------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_smoke)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseMotion motion when Input.MouseMode == Input.MouseModeEnum.Captured:
                _yaw -= motion.Relative.X * LookSensitivity;
                _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * LookSensitivity, -1.4f, 1.4f);
                _player.Rotation = new Vector3(_pitch, _yaw, 0f);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }:
                Input.MouseMode = Input.MouseModeEnum.Captured;
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key.Keycode);
                break;
        }
    }

    private void OnKey(Key key)
    {
        switch (key)
        {
            case Key.Escape:
                Input.MouseMode = Input.MouseModeEnum.Visible;
                break;
            case Key.Key1:
                ToggleThreat();
                break;
            case Key.Key2:
                _server.StartEvent(DemoProject.StingerEvent);
                break;
            case Key.Key3:
                _server.StartEvent(DemoProject.DialogueEvent);
                break;
            case Key.L:
                ToggleLocale();
                break;
            case Key.Space:
                _server.StartEvent(DemoProject.SandboxClickEvent);
                break;
            case Key.F9:
                _hud.Visible = !_hud.Visible;
                break;
        }
    }

    private void ToggleThreat()
    {
        _threatHigh = !_threatHigh;
        _server.SetGlobalParameter(DemoProject.ThreatParameter, _threatHigh ? 1f : 0f);
    }

    private void ToggleLocale()
    {
        _germanLocale = !_germanLocale;
        _server.System.Locale = _germanLocale ? "de" : "en";
    }

    private void Move(float dt)
    {
        var input = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) input.Z -= 1f;
        if (Input.IsKeyPressed(Key.S)) input.Z += 1f;
        if (Input.IsKeyPressed(Key.A)) input.X -= 1f;
        if (Input.IsKeyPressed(Key.D)) input.X += 1f;
        if (input == Vector3.Zero)
        {
            return;
        }

        Basis yawOnly = Basis.FromEuler(new Vector3(0f, _yaw, 0f));
        Vector3 world = yawOnly * input.Normalized() * MoveSpeed * dt;
        _player.Position += new Vector3(world.X, 0f, world.Z);
    }

    private void OrbitBirds(float dt)
    {
        _birdAngle += BirdOrbitSpeed * dt;
        Vector3 center = _player.GlobalPosition;
        var previous = _birdEmitter.GlobalPosition;
        var position = new Vector3(
            center.X + Mathf.Cos(_birdAngle) * BirdOrbitRadius,
            BirdOrbitHeight,
            center.Z + Mathf.Sin(_birdAngle) * BirdOrbitRadius);
        _birdEmitter.GlobalPosition = position;
        Vector3 velocity = dt > 0f ? (position - previous) / dt : Vector3.Zero;
        _birds?.Set3DAttributes(ToAttributes(_birdEmitter.GlobalTransform, new System.Numerics.Vector3(velocity.X, velocity.Y, velocity.Z)));
    }

    private void PushListener()
    {
        _server.System.SetListener(0, ToAttributes(_player.GlobalTransform, System.Numerics.Vector3.Zero));
    }

    private static Attributes3D ToAttributes(Transform3D transform, System.Numerics.Vector3 velocity)
    {
        Vector3 origin = transform.Origin;
        Vector3 forward = -transform.Basis.Z;
        Vector3 up = transform.Basis.Y;
        return new Attributes3D(
            new System.Numerics.Vector3(origin.X, origin.Y, origin.Z),
            velocity,
            new System.Numerics.Vector3(forward.X, forward.Y, forward.Z),
            new System.Numerics.Vector3(up.X, up.Y, up.Z));
    }

    private void UpdateHud()
    {
        if (!_hud.Visible || !_server.IsLoaded)
        {
            return;
        }

        DirgeSystem system = _server.System;
        double musicPosition = _music?.TimelinePosition ?? 0;
        string section = musicPosition >= DemoProject.CombatSectionStart ? "Combat" : "Calm";
        _hud.Text =
            $"Dirge sandbox{(_smoke ? " — smoke" : "")}\n" +
            $"[1] Threat {system.GetGlobalParameter(DemoProject.ThreatParameter):F2} → target {(_threatHigh ? 1 : 0)}   section {section} @ {musicPosition:F2}s\n" +
            $"[2] stinger (beat-quantized)   [3] dialogue   [L] locale {system.Locale}   [Space] click   [F9] hide HUD\n" +
            $"WASD move · mouse look (click to capture, Esc releases)\n" +
            $"Music bus {_musicLastDb:F1} dB · instances {system.InstanceCount} · voices {system.RealVoiceCount} · t={system.Time:F2}s";
    }

    // ---- smoke -----------------------------------------------------------------------------------------------------

    private void SmokeStep()
    {
        double t = _server.System.Time;
        try
        {
            switch (_smokeStep)
            {
                case 0 when t >= 0.5:
                    _server.StartEvent(DemoProject.SandboxClickEvent);
                    _smokeStep++;
                    break;
                case 1 when t >= 1.0:
                    ToggleThreat();
                    _smokeStep++;
                    break;
                case 2 when t >= 2.0:
                    _server.StartEvent(DemoProject.StingerEvent);
                    _smokeStep++;
                    break;
                case 3 when t >= 3.0:
                    _dialogueStarted = true;
                    _server.StartEvent(DemoProject.DialogueEvent);
                    _smokeStep++;
                    break;
                case 4 when t >= 4.0:
                    ToggleLocale();
                    _server.StartEvent(DemoProject.DialogueEvent);
                    _smokeStep++;
                    break;
                case 5 when t >= SmokeSeconds:
                    _smokeStep++;
                    _smoke = false;
                    Evaluate();
                    break;
            }
        }
        catch (Exception e)
        {
            _smoke = false;
            Fail($"exception at t={t:F3}: {e}");
        }
    }

    private void OnCommands(IReadOnlyList<Command> commands)
    {
        // Position after this tick's update; a jump/quantized start inside the tick leaves the playhead < dt past the grid point.
        double musicPhase = _music is null ? 0 : _music.TimelinePosition % DemoProject.BeatSeconds;
        for (int i = 0; i < commands.Count; i++)
        {
            Command command = commands[i];
            if (command is BusSetVolume { Bus: DemoProject.MusicBus } musicVolume)
            {
                _musicLastDb = musicVolume.VolumeDb;
            }

            if (!_smoke)
            {
                continue;
            }

            Type kind = command.GetType();
            _commandCounts[kind] = _commandCounts.TryGetValue(kind, out int n) ? n + 1 : 1;
            switch (command)
            {
                case VoicePlay play:
                    switch (play.Asset.Path)
                    {
                        case DemoProject.CombatDrumsAsset:
                        case DemoProject.CombatLeadAsset:
                            _combatPlays++;
                            _worstCombatPhase = Math.Max(_worstCombatPhase, musicPhase);
                            GD.Print($"SANDBOX_SMOKE combat layer {play.Asset.Path} at t={_server.System.Time:F3} musicPos={_music!.TimelinePosition:F4} phase={musicPhase:F4} start={play.StartSeconds:F3}");
                            break;
                        case DemoProject.StingerAsset:
                            _stingerPlays++;
                            _worstStingerPhase = Math.Max(_worstStingerPhase, musicPhase);
                            GD.Print($"SANDBOX_SMOKE stinger at t={_server.System.Time:F3} musicPos={_music!.TimelinePosition:F4} phase={musicPhase:F4}");
                            break;
                        case DemoProject.LineEnAsset:
                            _lineEnPlays++;
                            break;
                        case DemoProject.LineDeAsset:
                            _lineDePlays++;
                            break;
                        default:
                            if (play.Spatial) _birdPlays++;
                            break;
                    }

                    break;
                case BusSetVolume { Bus: DemoProject.MusicBus } volume when _dialogueStarted:
                    _musicMinDbDuringDialogue = Math.Min(_musicMinDbDuringDialogue, volume.VolumeDb);
                    break;
            }
        }
    }

    private void Evaluate()
    {
        _server.CommandsEmitted -= OnCommands;
        Recording recording = _recorder!.Stop();
        _recorder = null;

        foreach (KeyValuePair<Type, int> pair in _commandCounts)
        {
            GD.Print($"SANDBOX_SMOKE {pair.Key.Name} x{pair.Value}");
        }

        int totalCommands = 0;
        for (int i = 0; i < recording.Ticks.Count; i++)
        {
            totalCommands += recording.Ticks[i].Commands.Count;
        }

        GD.Print($"SANDBOX_SMOKE recording ticks={recording.Ticks.Count} commands={totalCommands} time={_server.System.Time:F3}s");
        GD.Print($"SANDBOX_SMOKE combatPlays={_combatPlays} worstCombatPhase={_worstCombatPhase:F4} stingerPlays={_stingerPlays} worstStingerPhase={_worstStingerPhase:F4}");
        GD.Print($"SANDBOX_SMOKE birdPlays={_birdPlays} lineEn={_lineEnPlays} lineDe={_lineDePlays} musicMinDbDuringDialogue={_musicMinDbDuringDialogue:F2}");

        // Round-trip the recording through JSON, then replay it on a fresh model fed only by the project's AssetLengths.
        string json = RecordingJson.Serialize(recording);
        using (FileAccess file = FileAccess.Open(RecordingFile, FileAccess.ModeFlags.Write))
        {
            file?.StoreString(json);
        }

        Recording loaded = RecordingJson.Deserialize(json);
        Recording replayed = Replayer.Replay(loaded, _server.System.Project, _server.Seed);
        Divergence? divergence = Replayer.Compare(recording, replayed);
        GD.Print($"SANDBOX_SMOKE replay ticks={replayed.Ticks.Count} compare={(divergence is null ? "identical" : divergence.ToString())} json={json.Length} chars → {ProjectSettings.GlobalizePath(RecordingFile)}");

        if (divergence is { } d)
        {
            Fail($"replay diverged at tick {d.Tick} index {d.Index}: expected {d.Expected} actual {d.Actual}");
            return;
        }

        if (_combatPlays < 2)
        {
            Fail($"expected both Combat layers to start, saw {_combatPlays} VoicePlay(s)");
            return;
        }

        if (_worstCombatPhase >= BeatPhaseTolerance)
        {
            Fail($"Combat layers started {_worstCombatPhase:F4}s past a beat (tolerance {BeatPhaseTolerance})");
            return;
        }

        if (_stingerPlays < 1)
        {
            Fail("stinger never played");
            return;
        }

        if (_worstStingerPhase >= BeatPhaseTolerance)
        {
            Fail($"stinger started {_worstStingerPhase:F4}s past a beat (tolerance {BeatPhaseTolerance})");
            return;
        }

        if (_lineEnPlays != 1 || _lineDePlays != 1)
        {
            Fail($"audio table resolution: expected one en and one de line, saw en={_lineEnPlays} de={_lineDePlays}");
            return;
        }

        if (!(_musicMinDbDuringDialogue <= -10f))
        {
            Fail($"Music bus only dipped to {_musicMinDbDuringDialogue:F2} dB during dialogue, expected <= -10 dB");
            return;
        }

        if (_birdPlays < 1)
        {
            Fail("no spatial bird voice played");
            return;
        }

        int musicBus = AudioServer.GetBusIndex(DemoProject.MusicBus);
        int dialogueBus = AudioServer.GetBusIndex(DemoProject.DialogueBus);
        if (musicBus < 0 || dialogueBus < 0)
        {
            Fail($"engine buses missing: Music={musicBus} Dialogue={dialogueBus}");
            return;
        }

        GD.Print("SANDBOX_SMOKE PASS");
        Quit(0);
    }

    private void Fail(string reason)
    {
        GD.PrintErr($"SANDBOX_SMOKE FAIL: {reason}");
        GD.Print($"SANDBOX_SMOKE FAIL: {reason}");
        Quit(1);
    }

    private static void EnsureProjectFile()
    {
        if (FileAccess.FileExists(ProjectFile))
        {
            return;
        }

        using FileAccess file = FileAccess.Open(ProjectFile, FileAccess.ModeFlags.Write);
        if (file is null)
        {
            GD.PushError($"Dirge sandbox: cannot write {ProjectFile}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreString(ProjectJson.Serialize(DemoProject.BuildSandbox()));
        GD.Print($"Dirge sandbox: generated {ProjectFile}");
    }
}
