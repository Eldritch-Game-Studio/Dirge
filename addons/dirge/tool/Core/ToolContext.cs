#nullable enable
using Godot;

namespace Dirge.Tool.Core;

/// <summary>Everything a pane needs. Created once by the shell; panes never construct services.</summary>
public sealed class ToolContext
{
    public ProjectDocument Project { get; }
    public Selection Selection { get; }
    public IAudition Audition { get; }
    /// <summary>Editor-side node panes may parent transient nodes under (audition players live here too).</summary>
    public Node Host { get; }

    public ToolContext(ProjectDocument project, Selection selection, IAudition audition, Node host)
    {
        Project = project; Selection = selection; Audition = audition; Host = host;
    }
}

/// <summary>A dockable panel. The shell parents <see cref="Root"/>; panes build their own content in <see cref="Attach"/>.</summary>
public interface IToolPane
{
    string Title { get; }
    Control Root { get; }
    void Attach(ToolContext context);
    void Detach();
}
