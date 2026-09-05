using System.Collections.Immutable;

using Desknav.ControlPlane;

namespace Desknav.ControlPlane.Tests;

public sealed class TargetLabelAllocatorTests
{
    [Fact]
    public void UsesHomeRowFirstErgonomicAlphabet()
    {
        var map = TargetLabelAllocator.Create(Snapshot(24));

        Assert.Equal(
            "asdfghjklqwertuiopxcvbnm".Select(
                static character => character.ToString()),
            map.Targets.Select(static target => target.Label.Value));
    }

    [Fact]
    public void AssignsLabelsInSpatialOrder()
    {
        var first = Target(1, 300, 300);
        var second = Target(2, -1000, -500);
        var third = Target(3, 100, 0);
        var fourth = Target(4, -1500, 0);
        var fifth = Target(5, 50, 100);
        var sixth = Target(6, 400, 100);
        var seventh = Target(7, 0, 300);
        var snapshot = Snapshot(
            first,
            second,
            third,
            fourth,
            fifth,
            sixth,
            seventh);

        var map = TargetLabelAllocator.Create(snapshot);

        Assert.Equal(
            ["a", "s", "d", "f", "g", "h", "j"],
            map.Targets
                .Select(static target => target.Label.Value)
                .ToArray());
        Assert.Equal(
            [
                second.Id,
                fourth.Id,
                third.Id,
                fifth.Id,
                sixth.Id,
                seventh.Id,
                first.Id,
            ],
            map.Targets
                .Select(static target => target.Target.Id)
                .ToArray());
    }

    [Fact]
    public void UsesBoundsThenTargetIdentityAsTieBreakers()
    {
        var first = new DesktopTarget(
            TargetId.From(new Guid(4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new TargetBounds(10, 20, 100, 100));
        var second = new DesktopTarget(
            TargetId.From(new Guid(3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new TargetBounds(10, 20, 50, 100));
        var third = new DesktopTarget(
            TargetId.From(new Guid(2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new TargetBounds(10, 20, 100, 50));
        var fourth = new DesktopTarget(
            TargetId.From(new Guid(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new TargetBounds(10, 20, 100, 100));

        var map = TargetLabelAllocator.Create(
            Snapshot(first, second, third, fourth));

        Assert.Equal(
            [second.Id, third.Id, fourth.Id, first.Id],
            map.Targets
                .Select(static target => target.Target.Id)
                .ToArray());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(24, 1)]
    [InlineData(25, 2)]
    [InlineData(576, 2)]
    [InlineData(577, 3)]
    public void UsesUniquePrefixFreeCodesAtLengthBoundaries(
        int targetCount,
        int expectedLength)
    {
        var map = TargetLabelAllocator.Create(Snapshot(targetCount));
        var labels = map.Targets
            .Select(static target => target.Label.Value)
            .ToArray();

        Assert.All(
            labels,
            label => Assert.Equal(expectedLength, label.Length));
        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    [Fact]
    public void DiscoveryOrderDoesNotChangeAssignments()
    {
        var targets = Enumerable
            .Range(1, 30)
            .Select(index => Target(index, index * 10, index * 20))
            .ToArray();
        var forward = TargetLabelAllocator.Create(Snapshot(targets));
        var reverse = TargetLabelAllocator.Create(
            Snapshot(targets.Reverse().ToArray()));

        Assert.Equal(forward, reverse);
    }

    private static TargetSnapshot Snapshot(params DesktopTarget[] targets) =>
        new(
            TargetDiscoveryRequestId.From(
                new Guid(1000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            [.. targets]);

    private static TargetSnapshot Snapshot(int targetCount) =>
        Snapshot(
            Enumerable
                .Range(1, targetCount)
                .Select(index => Target(index, index, 0))
                .ToArray());

    private static DesktopTarget Target(
        int id,
        int left,
        int top) =>
        new(
            TargetId.From(new Guid(id, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)),
            new TargetBounds(left, top, 100, 100));
}