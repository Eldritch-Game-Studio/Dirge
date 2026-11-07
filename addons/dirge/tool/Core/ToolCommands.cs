#nullable enable
namespace Dirge.Tool.Core;

/// <summary>Keyboard commands the shell routes to the focused pane before applying its own defaults.</summary>
public enum ToolCommand { Undo, Redo, Save, PlayStop, Delete, Copy, Paste, Duplicate, SelectAll, Escape }

/// <summary>Optional on an <see cref="IToolPane"/>: return <see langword="true"/> when the command was consumed.</summary>
public interface IToolPaneCommands
{
    bool Handle(ToolCommand command);
}
