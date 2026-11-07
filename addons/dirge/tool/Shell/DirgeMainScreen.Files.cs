#nullable enable
#if TOOLS
using System;
using System.Collections.Generic;
using Dirge.Project;
using Dirge.Tool.Core;
using Godot;

namespace Dirge.Tool.Shell;

public partial class DirgeMainScreen
{
    public const string ProjectExtension = ".dirge.json";
    private const string ProjectFilter = "*.dirge.json ; Dirge Project";

    private EditorFileDialog _openDialog = null!;
    private EditorFileDialog _saveDialog = null!;
    private ConfirmationDialog _discardDialog = null!;
    private AcceptDialog _errorDialog = null!;
    private ConfirmationDialog _connectDialog = null!;
    private LineEdit _connectHost = null!;
    private SpinBox _connectPort = null!;
    private Action? _afterDiscard;
    private bool _saveThenBuildBanks;

    private void CreateDialogs()
    {
        _openDialog = new EditorFileDialog
        {
            Name = "OpenDialog",
            FileMode = EditorFileDialog.FileModeEnum.OpenFile,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Title = "Open Dirge Project",
            DisplayMode = EditorFileDialog.DisplayModeEnum.List,
        };
        _openDialog.AddFilter(ProjectFilter);
        _openDialog.FileSelected += path => LoadProjectFile(path);
        AddChild(_openDialog);

        _saveDialog = new EditorFileDialog
        {
            Name = "SaveDialog",
            FileMode = EditorFileDialog.FileModeEnum.SaveFile,
            Access = EditorFileDialog.AccessEnum.Filesystem,
            Title = "Save Dirge Project As",
            DisplayMode = EditorFileDialog.DisplayModeEnum.List,
        };
        _saveDialog.AddFilter(ProjectFilter);
        _saveDialog.FileSelected += path => WriteProjectFile(path);
        AddChild(_saveDialog);

        _discardDialog = new ConfirmationDialog
        {
            Name = "DiscardDialog",
            Title = "Unsaved changes",
            DialogText = "The project has unsaved changes. Discard them?",
            OkButtonText = "Discard",
        };
        _discardDialog.Confirmed += () => { Action? next = _afterDiscard; _afterDiscard = null; next?.Invoke(); };
        _discardDialog.Canceled += () => _afterDiscard = null;
        AddChild(_discardDialog);

        _errorDialog = new AcceptDialog { Name = "ErrorDialog", Title = "Dirge" };
        AddChild(_errorDialog);

        _connectDialog = new ConfirmationDialog { Name = "ConnectDialog", Title = "Connect to running game", OkButtonText = "Connect" };
        var grid = new GridContainer { Columns = 2 };
        grid.AddChild(new Label { Text = "Host" });
        _connectHost = new LineEdit { Text = _live.Host, CustomMinimumSize = new Vector2(220, 0) };
        grid.AddChild(_connectHost);
        grid.AddChild(new Label { Text = "Port" });
        _connectPort = new SpinBox { MinValue = 1, MaxValue = 65535, Step = 1, Value = _live.Port, CustomMinimumSize = new Vector2(220, 0) };
        grid.AddChild(_connectPort);
        _connectDialog.AddChild(grid);
        _connectDialog.RegisterTextEnter(_connectHost);
        _connectDialog.Confirmed += () => _live.Connect(_connectHost.Text.Trim(), (int)_connectPort.Value);
        AddChild(_connectDialog);
    }

    private void ConfirmDiscard(Action action)
    {
        if (!_document.IsDirty)
        {
            action();
            return;
        }

        _afterDiscard = action;
        _discardDialog.PopupCentered();
    }

    public void NewProject() => ConfirmDiscard(() =>
    {
        _audition.StopAll(Runtime.StopMode.Immediate);
        _selection.Clear();
        _document.New();
        SetMessage("New project", false);
    });

    public void OpenProject() => ConfirmDiscard(() =>
    {
        if (_document.Path is { } current && current.Length > 0)
        {
            _openDialog.CurrentDir = current.GetBaseDir();
        }

        _openDialog.PopupCenteredRatio(0.6f);
    });

    /// <summary>Reads and parses <paramref name="path"/>; on failure the current project stays and an error dialog opens.</summary>
    public bool LoadProjectFile(string path)
    {
        string json = FileAccess.GetFileAsString(path);
        Error error = FileAccess.GetOpenError();
        if (error != Error.Ok)
        {
            ShowError($"Cannot read '{path}': {error}.");
            return false;
        }

        try
        {
            _audition.StopAll(Runtime.StopMode.Immediate);
            _selection.Clear();
            _document.LoadFromText(json, path);
        }
        catch (Exception exception)
        {
            ShowError($"'{path}' is not a valid Dirge project: {exception.GetBaseException().Message}");
            return false;
        }

        RememberLastProject(path);
        SetMessage($"Opened {path}", false);
        return true;
    }

    public void SaveProject(bool saveAs)
    {
        if (!saveAs && _document.Path is { } path && path.Length > 0)
        {
            WriteProjectFile(path);
            return;
        }

        if (_document.Path is { } current && current.Length > 0)
        {
            _saveDialog.CurrentPath = current;
        }
        else
        {
            string name = _document.Project.Name.Length > 0 ? _document.Project.Name : "untitled";
            _saveDialog.CurrentFile = SanitizeFileName(name) + ProjectExtension;
        }

        _saveDialog.PopupCenteredRatio(0.6f);
    }

    /// <summary>Serializes the document to <paramref name="path"/> (extension enforced) and clears the dirty flag.</summary>
    public bool WriteProjectFile(string path)
    {
        if (!path.EndsWith(ProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            path = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path[..^".json".Length] + ProjectExtension : path + ProjectExtension;
        }

        string json = _document.ToText();
        using (FileAccess? file = FileAccess.Open(path, FileAccess.ModeFlags.Write))
        {
            if (file is null)
            {
                ShowError($"Cannot write '{path}': {FileAccess.GetOpenError()}.");
                return false;
            }

            file.StoreString(json);
        }

        _document.MarkSaved(path);
        RememberLastProject(path);
        if (path.StartsWith("res://", StringComparison.Ordinal))
        {
            EditorInterface.Singleton.GetResourceFilesystem()?.UpdateFile(path);
        }

        RefreshDocumentUi();
        if (_saveThenBuildBanks)
        {
            _saveThenBuildBanks = false;
            ReportBanks(path);
        }
        else
        {
            SetMessage($"Saved {path}", false);
        }

        return true;
    }

    /// <summary>Banks are preload lists inside the project file; building them means persisting the project the runtime loads.</summary>
    public void BuildBanks()
    {
        if (_document.Path is { } path && path.Length > 0)
        {
            if (WriteProjectFile(path))
            {
                ReportBanks(path);
            }

            return;
        }

        _saveThenBuildBanks = true;
        SaveProject(saveAs: true);
    }

    private void ReportBanks(string path)
    {
        int events = 0;
        foreach (AudioBank bank in _document.Project.Banks)
        {
            events += bank.Events.Count;
        }

        SetMessage($"Built {_document.Project.Banks.Count} bank(s), {events} event reference(s) -> {path}", false);
    }

    public void ValidateProject()
    {
        _validation.Refresh(force: true);
        ShowTab(_validation);
        SetMessage(_validation.Diagnostics.Count == 0 ? "Validation: no issues" : $"Validation: {_validation.ErrorCount} error(s), {_validation.WarningCount} warning(s)", _validation.ErrorCount > 0);
    }

    public void ToggleLiveConnection()
    {
        if (_live.IsConnected)
        {
            _live.Disconnect();
            SetMessage("Live update disconnected", false);
            return;
        }

        _connectHost.Text = _live.Host;
        _connectPort.Value = _live.Port;
        _connectDialog.PopupCentered();
    }

    private void ShowError(string message)
    {
        GD.PushError("Dirge: " + message);
        SetMessage(message, true);
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    private static void RememberLastProject(string path)
    {
        EditorInterface.Singleton.GetEditorSettings()?.SetSetting(LastProjectSetting, path);
    }

    /// <summary>
    /// First-open behaviour: the editor's remembered project, else the <c>dirge/project_path</c> project setting, else the
    /// single <c>*.dirge.json</c> found under <c>res://</c>. Several candidates leave the tool empty with a hint.
    /// </summary>
    private void RestoreLastProject()
    {
        EditorSettings? settings = EditorInterface.Singleton.GetEditorSettings();
        if (settings is not null && settings.HasSetting(LastProjectSetting))
        {
            string remembered = settings.GetSetting(LastProjectSetting).AsString();
            if (remembered.Length > 0 && FileAccess.FileExists(remembered) && LoadProjectFile(remembered))
            {
                return;
            }
        }

        if (ProjectSettings.HasSetting(Driver.DirgeServer.ProjectPathSetting))
        {
            string configured = ProjectSettings.GetSetting(Driver.DirgeServer.ProjectPathSetting).AsString();
            if (configured.Length > 0 && FileAccess.FileExists(configured) && LoadProjectFile(configured))
            {
                return;
            }
        }

        var found = new List<string>();
        FindProjectFiles("res://", found);
        if (found.Count == 1)
        {
            LoadProjectFile(found[0]);
        }
        else if (found.Count > 1)
        {
            SetMessage($"{found.Count} Dirge projects in this project — File › Open… to choose one.", false);
        }
        else
        {
            SetMessage("No Dirge project yet — File › New, then Save As….", false);
        }
    }

    private static void FindProjectFiles(string directory, List<string> results)
    {
        using DirAccess? dir = DirAccess.Open(directory);
        if (dir is null)
        {
            return;
        }

        dir.ListDirBegin();
        for (string name = dir.GetNext(); name.Length > 0; name = dir.GetNext())
        {
            if (name.StartsWith('.'))
            {
                continue;
            }

            string path = directory.EndsWith('/') ? directory + name : directory + "/" + name;
            if (dir.CurrentIsDir())
            {
                if (name != "addons" && !FileAccess.FileExists(path + "/.gdignore"))
                {
                    FindProjectFiles(path, results);
                }
            }
            else if (name.EndsWith(".dirge.json", StringComparison.Ordinal))
            {
                results.Add(path);
            }
        }
        dir.ListDirEnd();
    }

    private static string SanitizeFileName(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buffer[i] = char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return new string(buffer).ToLowerInvariant();
    }
}
#endif
