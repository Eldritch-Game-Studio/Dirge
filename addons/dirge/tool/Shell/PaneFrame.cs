#nullable enable
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Hosts a pane's <c>Root</c> at the frame's full size while reporting only the shell's own minimum size (its
/// <c>CustomMinimumSize</c>, authored through <c>ToolDocuments.SetAuthoredMinimumSize</c>), so a pane's intrinsic minimum
/// (long labels, wide strips) can never push the status bar or sibling areas off screen; overflow is clipped and left to the
/// pane's own scrolling.
/// </summary>
[Tool]
public partial class PaneFrame : Container
{
    public PaneFrame()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Pass;
    }

    public override void _Notification(int what)
    {
        if (what == NotificationSortChildren)
        {
            var rect = new Rect2(Vector2.Zero, Size);
            int count = GetChildCount();
            for (int i = 0; i < count; i++)
            {
                // DocumentHost sizes itself from its parent (divided by the text scale); sizing it here would fight that.
                if (GetChild(i) is Control child && child.Visible && !child.TopLevel && child is not NecronomiDom.Runtime.DocumentHost)
                {
                    FitChildInRect(child, rect);
                }
            }
        }
    }
}
