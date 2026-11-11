using System;
using System.Collections.Generic;
using Dirge.Driver;
using Dirge.Runtime;
using Dirge.Serialization;
using Godot;

namespace DirgeDemo;

/// <summary>
/// Demo scene root. Interactive: wind loops, any key clicks, Enter toggles the Muffled snapshot.
/// With <c>-- --smoke</c> it runs the M1 T1 harness: scripted starts over 90 frames, then asserts on applied commands and
/// <see cref="AudioServer"/> state and exits 0/1 with a <c>DIRGE_SMOKE</c> line.
/// </summary>
public partial class Demo : Node
{
    public const string ProjectFile = "res://demo/demo.dirge.json";

    private DirgeServer _server = null!;
    private bool _smoke;
    private int _frame;
    private ulong _startMsec;
    private SnapshotInstance? _muffled;

    private readonly Dictionary<Type, int> _commandCounts = new();
    private VoiceId? _windVoice;
    private VoiceId? _clickVoice;
    private bool _clickFinishedFed;
    private bool _clickStopApplied;

    public override void _EnterTree()
    {
        // Runs before the child server's _Ready, so the generated project is on disk when it loads.
        EnsureProjectFile();
    }

    public override void _Ready()
    {
        _server = GetNode<DirgeServer>("DirgeServer");
        _smoke = Array.IndexOf(OS.GetCmdlineUserArgs(), "--smoke") >= 0;
        if (!_server.IsLoaded)
        {
            Fail("project failed to load");
            return;
        }

        _server.CommandsEmitted += OnCommands;
        _server.FeedbackSent += OnFeedback;
        _startMsec = Time.GetTicksMsec();

        if (_smoke)
        {
            Engine.MaxFps = 60;
            GD.Print("DIRGE_SMOKE start");
        }
        else
        {
            GD.Print("Dirge demo: any key = UI/Click, Enter = toggle Muffled snapshot.");
            _server.StartEvent(DemoProject.WindEvent);
        }
    }

    public override void _Process(double delta)
    {
        if (!_smoke)
        {
            return;
        }

        int frame = _frame++;
        try
        {
            switch (frame)
            {
                case 0:
                    _server.StartEvent(DemoProject.WindEvent);
                    break;
                case 10:
                    _server.StartEvent(DemoProject.ClickEvent);
                    break;
                case 20:
                    _server.StartSnapshot(DemoProject.MuffledSnapshot);
                    break;
            }
        }
        catch (Exception e)
        {
            _smoke = false;
            Fail($"exception at frame {frame}: {e}");
            return;
        }

        // 90 frames at the 60 fps cap ≈ 1.5 s; also wait for real time so the mixer thread has actually consumed the click.
        if (_frame >= 90 && Time.GetTicksMsec() - _startMsec >= 1500)
        {
            _smoke = false;
            Evaluate();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_smoke || @event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (key.Keycode == Key.Enter)
        {
            if (_muffled is null)
            {
                _muffled = _server.System.Mixer.StartSnapshot(DemoProject.MuffledSnapshot);
                GD.Print("Muffled on");
            }
            else
            {
                _muffled.Stop();
                _muffled = null;
                GD.Print("Muffled off");
            }
        }
        else
        {
            _server.StartEvent(DemoProject.ClickEvent);
        }
    }

    private void OnCommands(IReadOnlyList<Command> commands)
    {
        for (int i = 0; i < commands.Count; i++)
        {
            Command command = commands[i];
            Type kind = command.GetType();
            _commandCounts[kind] = _commandCounts.TryGetValue(kind, out int n) ? n + 1 : 1;

            switch (command)
            {
                case VoicePlay play when play.Asset.Path == DemoProject.WindAsset:
                    _windVoice = play.Voice;
                    break;
                case VoicePlay play when play.Asset.Path == DemoProject.ClickAsset:
                    _clickVoice = play.Voice;
                    break;
                case VoiceStop stop when _clickVoice is { } click && stop.Voice == click:
                    _clickStopApplied = true;
                    break;
            }
        }
    }

    private void OnFeedback(Feedback feedback)
    {
        if (feedback is VoiceFinished finished && _clickVoice is { } click && finished.Voice == click)
        {
            _clickFinishedFed = true;
        }
    }

    private void Evaluate()
    {
        foreach (KeyValuePair<Type, int> pair in _commandCounts)
        {
            GD.Print($"DIRGE_SMOKE {pair.Key.Name} x{pair.Value}");
        }

        int ambience = AudioServer.GetBusIndex(DemoProject.AmbienceBus);
        int master = AudioServer.GetBusIndex("Master");
        float ambienceDb = ambience >= 0 ? AudioServer.GetBusVolumeDb(ambience) : float.NaN;
        GD.Print($"DIRGE_SMOKE GetBusIndex(Master)={master} GetBusIndex(Ambience)={ambience} effects={(ambience >= 0 ? AudioServer.GetBusEffectCount(ambience) : -1)} GetBusVolumeDb(Ambience)={ambienceDb:F2}");
        GD.Print($"DIRGE_SMOKE wind voice={(_windVoice?.ToString() ?? "none")} playing={(_windVoice is { } w && _server.Driver.IsVoicePlaying(w))}");
        GD.Print($"DIRGE_SMOKE click voice={(_clickVoice?.ToString() ?? "none")} finishedFeedback={_clickFinishedFed} voiceStopApplied={_clickStopApplied} stillPlaying={(_clickVoice is { } c && _server.Driver.IsVoicePlaying(c))}");
        GD.Print($"DIRGE_SMOKE system time={_server.System.Time:F3}s instances={_server.System.InstanceCount} realVoices={_server.System.RealVoiceCount}");

        if (master != 0)
        {
            Fail($"Master bus index is {master}, expected 0");
            return;
        }

        if (ambience < 0)
        {
            Fail("Ambience bus missing");
            return;
        }

        if (AudioServer.GetBusEffectCount(ambience) != 1)
        {
            Fail($"Ambience has {AudioServer.GetBusEffectCount(ambience)} effects, expected 1");
            return;
        }

        if (AudioServer.GetBusEffect(ambience, 0) is not AudioEffectLowPassFilter)
        {
            Fail("Ambience effect 0 is not AudioEffectLowPassFilter");
            return;
        }

        if (_windVoice is not { } wind)
        {
            Fail("no VoicePlay for wind.wav");
            return;
        }

        if (!_server.Driver.IsVoicePlaying(wind))
        {
            Fail("wind voice is not playing");
            return;
        }

        if (_clickVoice is null)
        {
            Fail("no VoicePlay for click.wav");
            return;
        }

        if (!_clickFinishedFed && !_clickStopApplied)
        {
            Fail("click voice neither finished (feedback) nor stopped by the model");
            return;
        }

        if (!(ambienceDb <= DemoProject.MuffledAmbienceDb + 1f))
        {
            Fail($"Ambience volume {ambienceDb:F2} dB, expected <= {DemoProject.MuffledAmbienceDb + 1f} dB after Muffled");
            return;
        }

        try
        {
            VerifyAssetScanner();
        }
        catch (Exception error)
        {
            Fail($"asset scanner: {error.Message}");
            return;
        }

        GD.Print("DIRGE_SMOKE PASS");
        GetTree().Quit(0);
    }

    private static void VerifyAssetScanner()
    {
        string resource = "res://scanner-smoke-" + Guid.NewGuid().ToString("N");
        string directory = ProjectSettings.GlobalizePath(resource);
        string external = System.IO.Directory.CreateTempSubdirectory("dirge-scanner-").FullName;
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            System.IO.File.WriteAllBytes(directory + "/visible.wav", Array.Empty<byte>());
            System.IO.Directory.CreateDirectory(directory + "/ignored");
            System.IO.File.WriteAllText(directory + "/ignored/.gdignore", "");
            System.IO.File.WriteAllBytes(directory + "/ignored/hidden.wav", Array.Empty<byte>());
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.WriteAllBytes(external + "/outside.wav", Array.Empty<byte>());
                System.IO.Directory.CreateSymbolicLink(directory + "/linked", external);
                System.IO.File.CreateSymbolicLink(directory + "/linked.wav", external + "/outside.wav");
            }

            var scanner = new Dirge.Tool.Browser.AssetScanner();
            scanner.Scan();
            var found = new List<string>();
            foreach (string path in scanner.Files)
                if (path.StartsWith(resource + "/", StringComparison.Ordinal)) found.Add(path);
            if (found.Count != 1 || found[0] != resource + "/visible.wav")
                throw new InvalidOperationException("Ignored or linked audio escaped the scan boundary: " + string.Join(", ", found));
        }
        finally
        {
            System.IO.Directory.Delete(directory, true);
            System.IO.Directory.Delete(external, true);
        }
    }

    private void Fail(string reason)
    {
        GD.PrintErr($"DIRGE_SMOKE FAIL: {reason}");
        GD.Print($"DIRGE_SMOKE FAIL: {reason}");
        GetTree().Quit(1);
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
            GD.PushError($"Dirge demo: cannot write {ProjectFile}: {FileAccess.GetOpenError()}");
            return;
        }

        file.StoreString(ProjectJson.Serialize(DemoProject.Create()));
        GD.Print($"Dirge demo: generated {ProjectFile}");
    }
}
