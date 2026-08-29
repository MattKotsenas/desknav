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

    [Fact]
    public void GeneratedIdentifiersAreNonEmpty()
    {
        Assert.NotEqual(Guid.Empty, KanataConnectionId.New().Value);
        Assert.NotEqual(Guid.Empty, TargetDiscoveryRequestId.New().Value);
    }
}
