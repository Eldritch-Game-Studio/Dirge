#nullable enable
using System;
using Godot;

namespace Dirge.Tool.Events;

/// <summary>Opens the editor's resource file dialog (or a plain <see cref="FileDialog"/> in-game) filtered to audio streams.</summary>
internal sealed class AssetPicker
{
    private static readonly string[] Filters = { "*.wav ; WAV", "*.ogg ; Ogg Vorbis", "*.mp3 ; MP3" };

    private readonly Node _host;
    private ConfirmationDialog? _dialog;
    private Action<string>? _pending;

    public AssetPicker(Node host) => _host = host;

    public void Pick(string current, Action<string> picked)
    {
        _pending = picked;
        if (_dialog is null)
        {
            if (Engine.IsEditorHint())
            {
                var editorDialog = new EditorFileDialog { FileMode = EditorFileDialog.FileModeEnum.OpenFile, Access = EditorFileDialog.AccessEnum.Resources, Filters = Filters, Title = "Pick audio asset" };
                editorDialog.FileSelected += OnFile;
                _dialog = editorDialog;
            }
            else
            {
                var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Resources, Filters = Filters, Title = "Pick audio asset" };
                dialog.FileSelected += OnFile;
                _dialog = dialog;
            }
            _host.AddChild(_dialog);
        }
        if (current.Length > 0)
        {
            if (_dialog is EditorFileDialog e) e.CurrentPath = current;
            else if (_dialog is FileDialog f) f.CurrentPath = current;
        }
        _dialog.PopupCenteredRatio(0.6f);
    }

    private void OnFile(string path)
    {
        Action<string>? pending = _pending;
        _pending = null;
        pending?.Invoke(path);
    }

    public void Dispose()
    {
        _dialog?.QueueFree();
        _dialog = null;
    }
}
