using Desknav.ControlPlane;

using Vogen;

namespace Desknav.ControlPlane.Tests;

public sealed class ValueObjectTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void FrameSequenceRequiresPositiveValue(long value)
    {
        var exception = Assert.Throws<ValueObjectValidationException>(
            () => KanataFrameSequence.From(value));

        Assert.Contains(
            "positive",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkflowGenerationRequiresPositiveValue(long value)
    {
        var exception = Assert.Throws<ValueObjectValidationException>(
            () => WorkflowGeneration.From(value));

        Assert.Contains(
            "positive",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PresentationRevisionRequiresPositiveValue(long value)
    {
        var exception = Assert.Throws<ValueObjectValidationException>(
            () => PresentationRevision.From(value));

        Assert.Contains(
            "positive",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InitialPresentationRevisionPrecedesFirstAllocatedRevision()
    {
        Assert.Equal(0, PresentationRevision.Initial.Value);
        Assert.Equal(
            PresentationRevision.From(1),
            PresentationRevision.Initial.Increment());
    }

    [Theory]
    [InlineData(0, 1, "width")]
    [InlineData(-1, 1, "width")]
    [InlineData(1, 0, "height")]
    [InlineData(1, -1, "height")]
    public void TargetBoundsRequiresPositiveSize(
        int width,
        int height,
        string parameterName)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => new TargetBounds(0, 0, width, height));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public void TargetBoundsAllowsNegativeVirtualDesktopCoordinates()
    {
        var bounds = new TargetBounds(-1920, -1080, 640, 480);

        Assert.Equal(-1920, bounds.Left);
        Assert.Equal(-1080, bounds.Top);
    }

    [Fact]
    public void DesktopTargetRejectsDefaultBounds()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new DesktopTarget(TargetId.New(), default));

        Assert.Equal("bounds", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("fx")]
    public void TargetLabelAcceptsOnlyTheNavigationAlphabet(string value)
    {
        Assert.Throws<ValueObjectValidationException>(
            () => TargetLabel.From(value));
    }

    [Fact]
    public void TargetMapRejectsLabelsWithPrefixAmbiguity()
    {
        var targets = new[]
        {
            new DesktopTarget(
                TargetId.New(),
                new TargetBounds(0, 0, 100, 100)),
            new DesktopTarget(
                TargetId.New(),
                new TargetBounds(100, 0, 100, 100)),
        };

        var exception = Assert.Throws<ArgumentException>(
            () => new TargetMap(
                TargetDiscoveryRequestId.New(),
                [
                    new LabeledTarget(
                        TargetLabel.From("f"),
                        targets[0]),
                    new LabeledTarget(
                        TargetLabel.From("ff"),
                        targets[1]),
                ]));

        Assert.Equal("targets", exception.ParamName);
    }

    [Fact]
    public void GeneratedIdentifiersAreNonEmpty()
    {
        Assert.NotEqual(Guid.Empty, KanataConnectionId.New().Value);
        Assert.NotEqual(Guid.Empty, TargetDiscoveryRequestId.New().Value);
        Assert.NotEqual(Guid.Empty, TargetId.New().Value);
    }
}