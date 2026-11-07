using Dirge.Evaluation;
using Dirge.Project;
using NUnit.Framework;

namespace Dirge.Model.Tests.Mixer;

[TestFixture]
public sealed class PropertyBlendTests
{
    [Test]
    public void Blending_LerpsFromCurrent()
    {
        Assert.That(PropertyBlend.Apply(0f, 0f, -12f, 0.5f, SnapshotMode.Blending), Is.EqualTo(-6f));
        Assert.That(PropertyBlend.Apply(-12f, 0f, -6f, 0.5f, SnapshotMode.Blending), Is.EqualTo(-9f));
    }

    [Test]
    public void Overriding_LerpsFromAuthored()
    {
        Assert.That(PropertyBlend.Apply(-20f, 0f, -6f, 1f, SnapshotMode.Overriding), Is.EqualTo(-6f));
        Assert.That(PropertyBlend.Apply(-20f, 0f, -6f, 0.5f, SnapshotMode.Overriding), Is.EqualTo(-3f));
    }

    [Test]
    public void ZeroIntensity_LeavesCurrent_AndIntensityClamps()
    {
        Assert.That(PropertyBlend.Apply(-20f, 0f, -6f, 0f, SnapshotMode.Overriding), Is.EqualTo(-20f));
        Assert.That(PropertyBlend.Apply(0f, 0f, -6f, 3f, SnapshotMode.Blending), Is.EqualTo(-6f));
    }

    [Test]
    public void Compose_AppliesHighestPriorityLast()
    {
        SnapshotContribution[] sorted =
        {
            new(5, -6f, 0.5f, SnapshotMode.Blending),
            new(1, -12f, 1f, SnapshotMode.Blending),
        };
        Assert.That(PropertyBlend.Compose(0f, sorted), Is.EqualTo(-9f));

        sorted[0] = new SnapshotContribution(5, -6f, 1f, SnapshotMode.Overriding);
        Assert.That(PropertyBlend.Compose(0f, sorted), Is.EqualTo(-6f));
    }
}
