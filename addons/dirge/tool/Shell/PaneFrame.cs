#nullable enable
using Godot;

namespace Dirge.Tool.Shell;

/// <summary>
/// Hosts a pane's <c>Root</c> at the frame's full size while reporting only the shell's own minimum size, so a pane's
/// intrinsic minimum (long labels, wide strips) can never push the status bar or sibling areas off screen; overflow is clipped
/// and left to the pane's own scrolling.
/// </summary>
[Tool]
public partial class PaneFrame : Container
{
    private Vector2 _minimum;

    public PaneFrame()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>Minimum size the frame asks of its parent layout (independent of the pane inside).</summary>
    public Vector2 FrameMinimumSize
    {
        get => _minimum;
        set
        {
            _minimum = value;
            UpdateMinimumSize();
        }
    }

    public override Vector2 _GetMinimumSize() => _minimum;

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
