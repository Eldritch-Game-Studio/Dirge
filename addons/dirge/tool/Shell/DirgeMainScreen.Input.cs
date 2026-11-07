#nullable enable
#if TOOLS
using Dirge.Project;
using Dirge.Runtime;
using Dirge.Tool.Core;
using Godot;
using NecronomiDom.Dom;
using NecronomiDom.Runtime;

namespace Dirge.Tool.Shell;

public partial class DirgeMainScreen
{
    /// <summary>
    /// Shortcuts are taken in <c>_Input</c> rather than <c>_UnhandledKeyInput</c>: a focused <see cref="DocumentHost"/> marks
    /// every key as handled in its <c>_GuiInput</c>, so unhandled-input routing would silently stop working after any click
    /// into a document. Text fields keep their keys (except Ctrl+S); everything else goes to the focused pane, then to the shell.
    /// </summary>
    public override void _Input(Godot.InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key || !IsVisibleInTree())
        {
            return;
        }

        if (!TryMapCommand(key, out ToolCommand command))
        {
            return;
        }

        if (command != ToolCommand.Save && TextFieldHasFocus())
        {
            return;
        }

        Dispatch(command);
        GetViewport().SetInputAsHandled();
    }

    /// <summary>Routes a command to the focused pane first, then applies the shell default. Returns whether anything consumed it.</summary>
    public bool Dispatch(ToolCommand command)
    {
        if (FocusedPane() is IToolPaneCommands focused && focused.Handle(command))
        {
            return true;
        }

        return HandleDefault(command);
    }

    private static bool TryMapCommand(InputEventKey key, out ToolCommand command)
    {
        bool ctrl = key.CtrlPressed || key.MetaPressed;
        Key code = key.Keycode == Key.None ? key.PhysicalKeycode : key.Keycode;
        command = default;
        if (ctrl)
        {
            switch (code)
            {
                case Key.Z when key.ShiftPressed: command = ToolCommand.Redo; return true;
                case Key.Z: command = ToolCommand.Undo; return true;
                case Key.Y: command = ToolCommand.Redo; return true;
                case Key.S: command = ToolCommand.Save; return true;
                case Key.C: command = ToolCommand.Copy; return true;
                case Key.V: command = ToolCommand.Paste; return true;
                case Key.D: command = ToolCommand.Duplicate; return true;
                case Key.A: command = ToolCommand.SelectAll; return true;
                default: return false;
            }
        }

        if (key.AltPressed)
        {
            return false;
        }

        switch (code)
        {
            case Key.Space: command = ToolCommand.PlayStop; return true;
            case Key.Delete: command = ToolCommand.Delete; return true;
            case Key.Escape: command = ToolCommand.Escape; return true;
            default: return false;
        }
    }

    private bool TextFieldHasFocus()
    {
        Control? focus = GetViewport()?.GuiGetFocusOwner();
        return focus switch
        {
            LineEdit or TextEdit or SpinBox => true,
            DocumentHost host => host.Document?.FocusedElement is HTMLInputElement or HTMLTextAreaElement,
            _ => false,
        };
    }

    private bool HandleDefault(ToolCommand command)
    {
        switch (command)
        {
            case ToolCommand.Undo:
                if (_document.CanUndo) { Undo(); }
                return true;
            case ToolCommand.Redo:
                if (_document.CanRedo) { Redo(); }
                return true;
            case ToolCommand.Save:
                SaveProject(saveAs: false);
                return true;
            case ToolCommand.PlayStop:
                return PlayStop();
            case ToolCommand.Escape:
                if (_audition.Instances.Count > 0)
                {
                    _audition.StopAll(StopMode.Immediate);
                }
                else
                {
                    _selection.Clear();
                }

                return true;
            default:
                return false;
        }
    }

    public void Undo()
    {
        _document.UndoOne();
        SetMessage(_document.CanRedo ? "Undid " + _document.Undo.RedoLabel : "Undo", false);
    }

    public void Redo()
    {
        _document.RedoOne();
        SetMessage(_document.CanUndo ? "Redid " + _document.Undo.UndoLabel : "Redo", false);
    }

    /// <summary>Space: stop everything the tool started, else play the selected event (or the owner event of the selection).</summary>
    public bool PlayStop()
    {
        if (_audition.Instances.Count > 0)
        {
            _audition.StopAll(StopMode.AllowFadeout);
            return true;
        }

        DirgeId eventId = SelectedEventId();
        if (eventId.IsNone)
        {
            SetMessage("Select an event to play", false);
            return false;
        }

        if (_audition.Play(eventId) is null)
        {
            SetMessage("Event not available in the audition system yet (rebuilding)", true);
            return false;
        }

        return true;
    }

    private DirgeId SelectedEventId() => _selection.Kind switch
    {
        SelectionKind.Event => _selection.Id,
        SelectionKind.Instrument or SelectionKind.Track or SelectionKind.Marker => _selection.Owner,
        SelectionKind.Parameter when !_selection.Owner.IsNone => _selection.Owner,
        _ => DirgeId.None,
    };
}
#endif
