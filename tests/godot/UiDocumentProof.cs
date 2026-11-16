#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Dirge.Driver;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Paint;
using NecronomiDom.Runtime;

namespace Dirge.Tests;

/// <summary>
/// Headless installed-runtime proof for the managed tool UI against the locked NecronomiDOM: every document under
/// <see cref="ToolDocuments.UiRoot"/> and the in-game <see cref="DirgeOverlay"/> mount in real <see cref="DocumentHost"/>s,
/// lay out and paint without an exception or engine error, and each document's paint runs equal its PaintRun-JSON golden
/// under <c>tests/godot/golden/</c>. Prints <c>DIRGE_UI_PROOF PASS</c> and exits 0, or <c>DIRGE_UI_PROOF FAIL</c> and exits 1.
/// User arguments: <c>--update-goldens</c> rewrites the goldens; <c>--out=DIR</c> receives actual snapshots on mismatch.
/// </summary>
public partial class UiDocumentProof : Node
{
    public const string GoldenRoot = "res://tests/godot/golden/";
    public const string OverlayProject = "res://demo/sandbox/sandbox.dirge.json";
    /// <summary>Document viewport for layout and goldens: the tool's typical docked pane size at scale 1.</summary>
    public static readonly Vector2 DocumentSize = new(1280, 720);
    /// <summary>Game viewport for the overlay, which docks over the bottom half of it.</summary>
    public static readonly Vector2I OverlayViewport = new(1280, 720);

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<string> _failures = new();
    private readonly ErrorLog _log = new();
    private bool _updateGoldens;
    private string _output = "";

    /// <summary>Every engine error (push_error, unhandled callback exceptions, script errors) during the proof.</summary>
    private sealed partial class ErrorLog : Logger
    {
        private readonly List<string> _errors = new();

        public override void _LogError(string function, string file, int line, string code, string rationale, bool editorNotify,
            int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
        {
            if ((Logger.ErrorType)errorType == Logger.ErrorType.Warning) return;
            lock (_errors) _errors.Add($"{file}:{line} {function}: {(rationale.Length > 0 ? rationale : code)}");
        }

        public string[] Drain()
        {
            lock (_errors)
            {
                string[] errors = _errors.ToArray();
                _errors.Clear();
                return errors;
            }
        }
    }

    public override void _Ready()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == "--update-goldens") _updateGoldens = true;
            else if (argument.StartsWith("--out=", StringComparison.Ordinal)) _output = argument["--out=".Length..];
        }
        OS.AddLogger(_log);
        _ = Run();
    }

    private void Check(bool condition, string message)
    {
        if (condition) return;
        _failures.Add(message);
        GD.Print("DIRGE_UI_PROOF failure: " + message);
    }

    private async Task Frames(int count)
    {
        for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void DrainErrors(string phase)
    {
        foreach (string error in _log.Drain()) Check(false, $"{phase}: engine error {error}");
    }

    private async Task Run()
    {
        try
        {
            Check(ToolScale.Current == 1f, $"tool scale is authored (1), got {ToolScale.Current}");
            var layer = new CanvasLayer { Name = "Documents", Layer = 1 };
            AddChild(layer);
            string[] documents = DirAccess.GetFilesAt(ToolDocuments.UiRoot).Where(name => name.EndsWith(".html", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Check(documents.Length >= 10, $"tool UI documents discovered under {ToolDocuments.UiRoot} ({documents.Length})");
            foreach (string document in documents) await ProveDocument(layer, document);
            layer.QueueFree();
            await Frames(2);
            DrainErrors("document teardown");
            await ProveOverlay();
            if (!_updateGoldens) CheckNoStaleGoldens(documents);
        }
        catch (Exception error)
        {
            Check(false, "proof aborted: " + error);
        }
        DrainErrors("proof");
        OS.RemoveLogger(_log);
        GD.Print("DIRGE_UI_PROOF " + (_failures.Count == 0 ? "PASS" : $"FAIL ({_failures.Count})"));
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    /// <summary>Lays one document out and paints it synchronously so any unsupported-profile exception surfaces here,
    /// then lets the host's own frame loop run before comparing paint runs with the golden.</summary>
    private async Task ProveDocument(CanvasLayer layer, string document)
    {
        var slot = new Control { Name = document.Replace('.', '_') + "_slot", Size = DocumentSize, ClipContents = true };
        layer.AddChild(slot);
        DocumentHost host;
        try
        {
            host = ToolDocuments.Create(document);
            slot.AddChild(host);
            host.FlushPendingLayout();
        }
        catch (Exception error)
        {
            Check(false, $"{document}: mount/layout threw {error.GetType().Name}: {error.Message}");
            slot.QueueFree();
            return;
        }
        await Frames(3);
        try
        {
            host.FlushPendingLayout();
        }
        catch (Exception error)
        {
            Check(false, $"{document}: layout threw {error.GetType().Name}: {error.Message}");
        }
        DrainErrors(document);
        Check(host.Size == DocumentSize, $"{document}: host fills its slot ({host.Size})");
        Check(host.LayoutRevision > 0, $"{document}: layout ran");
        Check(host.PaintRevision > 0, $"{document}: paint ran");
        Check(host.DisplayList.Runs.Count > 0, $"{document}: display list has paint runs");
        Check(host.PaintedBoxCount + host.PaintedTextRunCount > 0, $"{document}: native paint produced boxes or text");
        CompareGolden(document, Snapshot(document, host));
        slot.QueueFree();
    }

    private static string Snapshot(string document, DocumentHost host)
    {
        var snapshot = new Dictionary<string, object>
        {
            ["document"] = document,
            ["width"] = host.Size.X,
            ["height"] = host.Size.Y,
            ["runs"] = host.DisplayList.Runs.Select(PaintRunJson.ToElement).ToArray(),
        };
        return JsonSerializer.Serialize(snapshot, SnapshotOptions).Replace("\r\n", "\n") + "\n";
    }

    private void CompareGolden(string document, string actual)
    {
        string golden = ProjectSettings.GlobalizePath(GoldenRoot + Path.ChangeExtension(document, ".json"));
        if (_updateGoldens)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, actual);
            GD.Print($"DIRGE_UI_PROOF golden written {golden}");
            return;
        }
        if (!File.Exists(golden))
        {
            Check(false, $"{document}: golden {golden} missing (run scripts/ui-proof.sh --update-goldens and review it)");
            return;
        }
        string expected = File.ReadAllText(golden).Replace("\r\n", "\n");
        if (expected == actual) return;
        string[] want = expected.Split('\n'), got = actual.Split('\n');
        int line = 0;
        while (line < want.Length && line < got.Length && want[line] == got[line]) line++;
        string Line(string[] lines) => line < lines.Length ? lines[line].Trim() : "<end>";
        string where = "";
        if (_output.Length > 0)
        {
            Directory.CreateDirectory(_output);
            where = Path.Combine(_output, Path.ChangeExtension(document, ".json"));
            File.WriteAllText(where, actual);
            where = $"; actual written to {where}";
        }
        Check(false, $"{document}: paint runs differ from golden at line {line + 1}: expected `{Line(want)}`, got `{Line(got)}`{where}");
    }

    private void CheckNoStaleGoldens(string[] documents)
    {
        var expected = documents.Select(document => Path.ChangeExtension(document, ".json")).ToHashSet(StringComparer.Ordinal);
        if (!DirAccess.DirExistsAbsolute(GoldenRoot)) return;
        foreach (string golden in DirAccess.GetFilesAt(GoldenRoot).Where(name => name.EndsWith(".json", StringComparison.Ordinal)))
        {
            Check(expected.Contains(golden), $"golden {golden} has no tool UI document");
        }
    }

    /// <summary>The overlay's Mixer and Profiler panes, attached to a loaded project, each mount a laid-out document host.</summary>
    private async Task ProveOverlay()
    {
        // The overlay fills the game viewport; the headless display server starts with a placeholder 64x64 window.
        GetWindow().Size = OverlayViewport;
        await Frames(1);
        Check(GetViewport().GetVisibleRect().Size == (Vector2)OverlayViewport,
            $"game viewport is {OverlayViewport} for the overlay ({GetViewport().GetVisibleRect().Size})");
        var server = new DirgeServer { Name = "DirgeServer", ProjectPath = OverlayProject, LiveUpdatePort = 0 };
        AddChild(server);
        Check(server.IsLoaded, $"overlay server loaded {OverlayProject}");
        var overlay = new DirgeOverlay { Name = "DirgeOverlay" };
        AddChild(overlay);
        await Frames(1);
        overlay.ShowOverlay();
        Check(overlay.IsShown, "overlay shown");
        await Frames(3);
        DrainErrors("overlay mount");
        var tabs = overlay.FindChild("Tabs", true, false) as TabContainer;
        Check(tabs is { } && tabs.GetTabCount() == 2, "overlay mounted Mixer and Profiler tabs");
        if (tabs is null)
        {
            return;
        }
        for (int tab = 0; tab < tabs.GetTabCount(); tab++)
        {
            tabs.CurrentTab = tab;
            await Frames(3);
            string title = tabs.GetTabTitle(tab);
            var hosts = Hosts(tabs.GetTabControl(tab)).ToArray();
            Check(hosts.Length > 0, $"overlay {title}: pane mounts a DocumentHost");
            foreach (DocumentHost host in hosts)
            {
                try
                {
                    host.FlushPendingLayout();
                }
                catch (Exception error)
                {
                    Check(false, $"overlay {title} {host.DocumentPath}: layout threw {error.GetType().Name}: {error.Message}");
                }
                Check(host.Size.X > 0 && host.Size.Y > 0, $"overlay {title} {host.DocumentPath}: host has a size ({host.Size}; {SizeChain(host)})");
                Check(host.LayoutRevision > 0 && host.DisplayList.Runs.Count > 0, $"overlay {title} {host.DocumentPath}: laid out");
                Check(host.PaintRevision > 0, $"overlay {title} {host.DocumentPath}: painted");
            }
            DrainErrors("overlay " + title);
        }
        overlay.HideOverlay();
        overlay.QueueFree();
        server.QueueFree();
        await Frames(2);
        DrainErrors("overlay teardown");
    }

    /// <summary>Ancestor control sizes up to the first non-Control, for diagnosing a host that was given no area.</summary>
    private static string SizeChain(Node node)
    {
        var parts = new List<string>();
        for (Node? current = node.GetParent(); current is Control control; current = current.GetParent())
        {
            parts.Add($"{control.Name} {control.Size} visible={control.Visible}");
        }
        return string.Join(" < ", parts);
    }

    private static IEnumerable<DocumentHost> Hosts(Node root)
    {
        if (root is DocumentHost host) yield return host;
        foreach (Node child in root.GetChildren())
        {
            foreach (DocumentHost nested in Hosts(child)) yield return nested;
        }
    }
}
