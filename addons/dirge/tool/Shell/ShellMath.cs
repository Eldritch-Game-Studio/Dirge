#nullable enable
using Dirge.Runtime;
using Godot;

namespace Dirge.Tool.Shell;

internal static class ShellMath
{
    /// <summary>Engine transform for model 3D attributes (−Z forward); identity basis when the vectors are degenerate.</summary>
    public static Transform3D ToTransform(Attributes3D attributes)
    {
        var position = new Vector3(attributes.Position.X, attributes.Position.Y, attributes.Position.Z);
        var forward = new Vector3(attributes.Forward.X, attributes.Forward.Y, attributes.Forward.Z);
        var up = new Vector3(attributes.Up.X, attributes.Up.Y, attributes.Up.Z);
        Basis basis = forward.IsZeroApprox() || up.IsZeroApprox() || forward.Cross(up).IsZeroApprox()
            ? Basis.Identity
            : Basis.LookingAt(forward, up);
        return new Transform3D(basis, position);
    }
}
